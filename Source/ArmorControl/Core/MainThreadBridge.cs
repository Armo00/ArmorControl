using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ArmorOverhaul.ArmorControl.Protocol;

namespace ArmorOverhaul.ArmorControl.Core
{
    internal sealed class MainThreadBridge
    {
        private readonly ConcurrentQueue<ControlCommand> commands = new ConcurrentQueue<ControlCommand>();
        private readonly object commandDedupSync = new object();
        private readonly Dictionary<string, PendingCommand> pendingCommands = new Dictionary<string, PendingCommand>(StringComparer.Ordinal);
        private readonly Dictionary<string, CommandResult> completedCommands = new Dictionary<string, CommandResult>(StringComparer.Ordinal);
        private readonly Queue<string> completedCommandOrder = new Queue<string>();
        private TelemetrySnapshot latest = TelemetrySnapshot.Empty;
        private RegularTelemetrySnapshot latestRegular = RegularTelemetrySnapshot.Empty;
        private RuntimeMetrics latestMetrics = RuntimeMetrics.Empty;
        private VesselStructureSnapshot latestStructure = VesselStructureSnapshot.Empty;
        private VesselImageSnapshot latestVesselImage = VesselImageSnapshot.Empty;
        private readonly object recorderSync = new object();
        private readonly FlightRecorderSample[] recorderSamples = new FlightRecorderSample[3000];
        private FlightRecorderSample latestRecorder;
        private int recorderStart;
        private int recorderCount;
        private long recorderRevision;
        private FlightPanelSnapshot latestFlightPanel;
        private AutomationSnapshot latestAutomation;
        private PorkchopResultSnapshot latestPorkchop = PorkchopResultSnapshot.Empty;
        private long commandSequence;

        internal Func<IncomingMessage, CommandExecution> CommandHandler { get; set; }

        internal TelemetrySnapshot LatestSnapshot
        {
            get { return Volatile.Read(ref latest); }
        }

        internal RegularTelemetrySnapshot LatestRegularSnapshot
        {
            get { return Volatile.Read(ref latestRegular); }
        }

        internal RuntimeMetrics LatestMetrics
        {
            get { return Volatile.Read(ref latestMetrics); }
        }

        internal VesselStructureSnapshot LatestStructure
        {
            get { return Volatile.Read(ref latestStructure); }
        }

        internal VesselImageSnapshot LatestVesselImage
        {
            get { return Volatile.Read(ref latestVesselImage); }
        }

        internal FlightRecorderSample LatestRecorder
        {
            get { return Volatile.Read(ref latestRecorder); }
        }

        internal FlightPanelSnapshot LatestFlightPanel
        {
            get { return Volatile.Read(ref latestFlightPanel); }
        }

        internal AutomationSnapshot LatestAutomation
        {
            get { return Volatile.Read(ref latestAutomation); }
        }

        internal PorkchopResultSnapshot LatestPorkchop
        {
            get { return Volatile.Read(ref latestPorkchop); }
        }

        internal void Publish(TelemetrySnapshot snapshot)
        {
            Interlocked.Exchange(ref latest, snapshot);
        }

        internal void PublishRegular(RegularTelemetrySnapshot snapshot)
        {
            Interlocked.Exchange(ref latestRegular, snapshot);
        }

        internal void PublishMetrics(RuntimeMetrics metrics)
        {
            Interlocked.Exchange(ref latestMetrics, metrics);
        }

        internal void PublishStructure(VesselStructureSnapshot snapshot)
        {
            Interlocked.Exchange(ref latestStructure, snapshot);
        }

        internal void PublishVesselImage(VesselImageSnapshot snapshot)
        {
            Interlocked.Exchange(ref latestVesselImage, snapshot ?? VesselImageSnapshot.Empty);
        }

        internal void PublishRecorder(FlightRecorderSample sample, bool reset)
        {
            lock (recorderSync)
            {
                if (reset)
                {
                    Array.Clear(recorderSamples, 0, recorderSamples.Length);
                    recorderStart = 0;
                    recorderCount = 0;
                    recorderRevision++;
                }
                int index;
                if (recorderCount < recorderSamples.Length)
                {
                    index = (recorderStart + recorderCount) % recorderSamples.Length;
                    recorderCount++;
                }
                else
                {
                    index = recorderStart;
                    recorderStart = (recorderStart + 1) % recorderSamples.Length;
                }
                sample.Revision = recorderRevision;
                recorderSamples[index] = sample;
                Volatile.Write(ref latestRecorder, sample);
            }
        }

        internal long RecorderRevision
        {
            get { lock (recorderSync) return recorderRevision; }
        }

        internal void ResetRecorder()
        {
            lock (recorderSync)
            {
                Array.Clear(recorderSamples, 0, recorderSamples.Length);
                recorderStart = 0;
                recorderCount = 0;
                recorderRevision++;
                Volatile.Write(ref latestRecorder, null);
            }
        }

        internal FlightRecorderHistory GetRecorderHistory()
        {
            lock (recorderSync)
            {
                var copy = new FlightRecorderSample[recorderCount];
                for (int index = 0; index < recorderCount; index++)
                {
                    copy[index] = recorderSamples[(recorderStart + index) % recorderSamples.Length];
                }
                return new FlightRecorderHistory(recorderRevision, copy);
            }
        }

        internal void PublishFlightPanel(FlightPanelSnapshot snapshot)
        {
            Interlocked.Exchange(ref latestFlightPanel, snapshot);
        }

        internal void PublishAutomation(AutomationSnapshot snapshot)
        {
            Interlocked.Exchange(ref latestAutomation, snapshot);
        }

        internal void PublishPorkchop(PorkchopResultSnapshot snapshot)
        {
            Interlocked.Exchange(ref latestPorkchop, snapshot);
        }

        internal long Enqueue(string clientId, IncomingMessage message, Action<CommandResult> complete)
        {
            CommandResult completed = null;
            long sequence;
            lock (commandDedupSync)
            {
                if (completedCommands.TryGetValue(message.CommandId, out completed))
                {
                    sequence = completed.ServerSequence;
                }
                else
                {
                    PendingCommand pending;
                    if (pendingCommands.TryGetValue(message.CommandId, out pending))
                    {
                        pending.Callbacks.Add(complete);
                        return pending.ServerSequence;
                    }

                    sequence = Interlocked.Increment(ref commandSequence);
                    pendingCommands.Add(message.CommandId, new PendingCommand(sequence, complete));
                    commands.Enqueue(new ControlCommand(clientId, sequence, message));
                }
            }

            if (completed != null) CompleteQuietly(complete, completed);
            return sequence;
        }

        internal int DrainCommands(int maximum)
        {
            int processed = 0;
            ControlCommand command;
            while (processed < maximum && commands.TryDequeue(out command))
            {
                processed++;
                CommandResult result;
                if (command.Message.Name == "noop")
                {
                    result = new CommandResult
                    {
                        CommandId = command.Message.CommandId,
                        ServerSequence = command.ServerSequence,
                        Success = true,
                        Code = "ok",
                        Message = "FIFO command processed on the KSP main thread."
                    };
                }
                else if (CommandHandler != null)
                {
                    CommandExecution execution;
                    try
                    {
                        execution = CommandHandler(command.Message);
                    }
                    catch (Exception exception)
                    {
                        execution = new CommandExecution { Success = false, Code = "command_exception", Message = exception.Message };
                    }
                    result = new CommandResult
                    {
                        CommandId = command.Message.CommandId,
                        ServerSequence = command.ServerSequence,
                        Success = execution != null && execution.Success,
                        Code = execution == null ? "command_rejected" : execution.Code,
                        Message = execution == null ? "Command handler rejected the request." : execution.Message
                    };
                }
                else
                {
                    result = new CommandResult
                    {
                        CommandId = command.Message.CommandId,
                        ServerSequence = command.ServerSequence,
                        Success = false,
                        Code = "not_implemented",
                        Message = "The command is not available in milestone M1."
                    };
                }

                CompleteCommand(command.Message.CommandId, result);
            }
            return processed;
        }

        private void CompleteCommand(string commandId, CommandResult result)
        {
            List<Action<CommandResult>> callbacks = null;
            lock (commandDedupSync)
            {
                PendingCommand pending;
                if (pendingCommands.TryGetValue(commandId, out pending))
                {
                    callbacks = pending.Callbacks;
                    pendingCommands.Remove(commandId);
                }
                completedCommands[commandId] = result;
                completedCommandOrder.Enqueue(commandId);
                while (completedCommandOrder.Count > 2048)
                {
                    string expired = completedCommandOrder.Dequeue();
                    completedCommands.Remove(expired);
                }
            }

            if (callbacks == null) return;
            foreach (Action<CommandResult> callback in callbacks) CompleteQuietly(callback, result);
        }

        private static void CompleteQuietly(Action<CommandResult> callback, CommandResult result)
        {
            try { callback(result); }
            catch { /* The originating client may have disconnected after enqueueing. */ }
        }

        private sealed class ControlCommand
        {
            internal ControlCommand(string clientId, long serverSequence, IncomingMessage message)
            {
                ClientId = clientId;
                ServerSequence = serverSequence;
                Message = message;
            }

            internal string ClientId { get; private set; }
            internal long ServerSequence { get; private set; }
            internal IncomingMessage Message { get; private set; }
        }

        private sealed class PendingCommand
        {
            internal PendingCommand(long serverSequence, Action<CommandResult> callback)
            {
                ServerSequence = serverSequence;
                Callbacks = new List<Action<CommandResult>> { callback };
            }

            internal long ServerSequence { get; private set; }
            internal List<Action<CommandResult>> Callbacks { get; private set; }
        }
    }
}
