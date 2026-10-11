using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArmorOverhaul.ArmorControl.Core;
using ArmorOverhaul.ArmorControl.Protocol;
using ArmorOverhaul.ArmorControl.Transport;

internal static class Program
{
    private sealed class InternalTrajectoryPatch
    {
        internal double EndTime { get { return 123; } }
        internal double pos = 456;
    }
    private sealed class TestB9Subtype
    {
        public bool allowSwitchInFlight = true, allowSwitchFromInFlight = true, Unlocked = true;
        public bool IsUnlocked() { return Unlocked; }
    }
    private sealed class TestB9Switcher
    {
        public bool switchInFlight = true;
        public TestB9Subtype CurrentSubtype { get; set; }
    }

    private static async Task<int> Main()
    {
        PartFlightEventsTests.Run();
        LocalizationTests.Run();
        SettingsTests.Run();
        Assert((double)TrajectoryMemberAccess.Read(new InternalTrajectoryPatch(), "EndTime") == 123, "internal trajectory property access");
        Assert((double)TrajectoryMemberAccess.Read(new InternalTrajectoryPatch(), "pos") == 456, "internal trajectory field access");
        Assert(TrajectoryMemberAccess.Read(null, "pos") == null, "null trajectory patch");
        Console.WriteLine("PASS Trajectories internal properties and fields (empty-path regression)");
        var subtype = new TestB9Subtype();
        var switcher = new TestB9Switcher { CurrentSubtype = subtype };
        Assert(B9SwitchPolicy.CanSwitchFrom(switcher) && B9SwitchPolicy.CanSwitchTo(subtype), "allowed B9 flight switch");
        subtype.allowSwitchFromInFlight = false;
        Assert(!B9SwitchPolicy.CanSwitchFrom(switcher), "B9 current subtype forbids switching away");
        subtype.allowSwitchFromInFlight = true;
        switcher.switchInFlight = false;
        Assert(!B9SwitchPolicy.CanSwitchFrom(switcher), "B9 module flight switch disabled");
        subtype.allowSwitchInFlight = false;
        Assert(!B9SwitchPolicy.CanSwitchTo(subtype), "B9 destination forbids flight switch");
        subtype.allowSwitchInFlight = true;
        subtype.Unlocked = false;
        Assert(!B9SwitchPolicy.CanSwitchTo(subtype), "B9 destination is locked");
        Assert(!B9SwitchPolicy.CanSwitchFrom(null) && !B9SwitchPolicy.CanSwitchTo(new object()), "unknown B9 API fails closed");
        Console.WriteLine("PASS B9 flight subtype policy + unlock checks");
        string root = Path.Combine(Path.GetTempPath(), "ArmorControlTransportTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), "<h1>ArmorControl Test</h1>", Encoding.UTF8);
        await TestServerLanguage(root);

        int port = FindFreePort();
        var bridge = new MainThreadBridge();
        IncomingMessage handledCommand = null;
        int handledCommandCount = 0;
        bridge.CommandHandler = message => {
            handledCommand = message;
            handledCommandCount++;
            return new CommandExecution { Success = true, Code = "ok", Message = "test handler" };
        };
        using (var server = new ArmorControlServer(port, root, bridge))
        using (var first = new ClientWebSocket())
        using (var second = new ClientWebSocket())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            try
            {
                bridge.PublishMetrics(new RuntimeMetrics(90, .12, .45, 15, .31, .8));
                bridge.PublishStructure(new VesselStructureSnapshot(
                    3, "vessel-1", "Test Vessel", 3,
                    new[] { new CrewMemberSnapshot("Valentina Kerman", "Pilot", 4, "Command Pod", 100, true, null) },
                    new[] {
                        new PartSnapshot(100, null, "mk1pod", "Command Pod", 0, .8, 300, 2400, 310, 2400,
                            new[] { new ResourceSnapshot("ElectricCharge", 48, 50) }, new[] { "ModuleCommand", "ModuleEnginesFX", "ModuleRCSFX" },
                            new[] { new EngineSnapshot(1, "main", "Main Engine", true, false, true, 72, -1, false) },
                            new[] { new PartToggleSnapshot("rcs", 2, "RCS", true, false) }),
                        new PartSnapshot(101, 100, "tank", "Fuel Tank", 1, 1.2, 290, 2000, 295, 2000,
                            new[] { new ResourceSnapshot("LiquidFuel", 80, 90) }, new string[0])
                    }, new AerodynamicCenterSnapshot(true, true, "FAR 0.16.1.2", "Current FAR aerodynamic force center.",
                        .44, .61, 1.8, -1.7, .2, .1)));
                bridge.PublishRecorder(RecorderSample(1, 0.2, 3), true);
                bridge.PublishFlightPanel(new FlightPanelSnapshot {
                    Sequence = 5, VesselId = "vessel-1", CurrentOrbit = "Kerbin 120 x 85 km",
                    OrbitInclination = 2.1, SurfaceSpeed = 2300, HorizontalSurfaceSpeed = 2298,
                    AngleOfAttack = 2.4, AngleOfSideslip = -.3, StaticPressureKpa = 18.1,
                    DynamicPressureKpa = 21.4, CurrentBiome = "Kerbin Shores",
                    SuicideBurnCountdown = "T- 42.1 s", TargetDistance = "18.4 km", PartCount = 184
                });
                bridge.PublishAutomation(new AutomationSnapshot {
                    Sequence = 9, VesselId = "vessel-1", MechJebAvailable = true,
                    OrbitGeometryJson = "{\"radius\":600000,\"current\":[[700000,0,0]],\"planned\":[[800000,0,0]]}",
                    ManeuverNodeCount = 2, NextNodeUt = 12345, NextNodeDeltaV = 312.4,
                    ManeuverNodes = new[] { new ManeuverNodeSnapshot(12345, 312.4), new ManeuverNodeSnapshot(12500, 26.8) },
                    SmartAssEnabled = true, SmartAssMode = "PROGRADE", SmartAssAutoDisable = true,
                    SmartAssSurfacePitch = 30, SmartAssSurfaceYaw = 90, SmartAssSurfaceRoll = 180,
                    SmartAssVelocityPitch = 5, SmartAssVelocityYaw = 10, SmartAssVelocityRoll = 15, SmartAssRoll = 45,
                    LandingPredictionReady = true, LandingStatus = "Coasting toward deceleration burn",
                    LandingStep = "CoastToDeceleration", LandingDescentMode = "SafeDescentSpeedPolicy", LandingUsesAtmosphere = true,
                    LandingTouchdownSpeed = .5, LandingDeployGears = true, LandingGearStageLimit = 2,
                    LandingDeployChutes = true, LandingChuteStageLimit = 1, LandingRcsAdjustment = true,
                    LandingPredictionsEnabled = true, LandingPredictionOutcome = "LANDED",
                    LandingPredictedLatitude = -.097, LandingPredictedLongitude = -74.558, LandingPredictedAltitude = 72,
                    LandingPredictionTargetDistance = 38.4, LandingPredictionMaxDrag = 4.2,
                    LandingPredictionDeltaV = 812.5, LandingPredictionTimeToLand = 143.2,
                    LandingShowTrajectory = true, LandingWorldTrajectory = true,
                    DockingStatus = "Ready", DockingStep = "OFF",
                    AscentLaunchMode = "TARGET_PLANE", AscentLaunchTargetAvailable = true,
                    AscentLaunchTargetName = "Target Station", AscentLaunchLanDifference = 2.5,
                    DockingAxialSeparation = 8.42, DockingLateralSeparation = .18, DockingAxisAvailable = true,
                    DockingAxisErrorX = .12, DockingAxisErrorY = -.134, TargetName = "Target Station",
                    DockingSpeedLimit = 1.25, DockingOverrideSafeDistance = true, DockingSafeDistance = 6.5,
                    DockingOverrideStartDistance = true, DockingStartDistance = 12.0,
                    DockingForceRoll = true, DockingRoll = 90, DockingDrawBoundingBox = true,
                    PositionTargetExists = true, TargetBody = "Kerbin", TargetLatitude = -.0972, TargetLongitude = -74.5577,
                    Sas = true, Rcs = true, Lights = true, ActionGroupMask = 4,
                    SolarPanels = "extended", Antennas = "retracted", AutoStage = true,
                    PorkchopRevision = 4, PorkchopStatus = "ready", PorkchopProgress = 100,
                    EnvelopeAvailable = true, EnvelopeDynamicPressure = true, EnvelopeMaximumDynamicPressure = 42000,
                    EnvelopeAcceleration = true, EnvelopeMaximumAcceleration = 29.42,
                    EnvelopeMinimumThrottle = true, EnvelopeMinimumThrottleValue = .25,
                    EnvelopePreventOverheat = true, EnvelopePreventFlameout = true,
                    Trajectory = new TrajectoryPredictionSnapshot {
                        Available = true, Version = "2.4.5", Status = "Prediction ready.",
                        PathsJson = "[[[700000,0,0],[600000,1,0]]]",
                        GroundPathsJson = "[[[19,110,100],[20,111,0]]]", SurfaceMapVersion = 1, BodyRadius = 6371000, ImpactTargetDistance = 1234,
                        DisplayTrajectories = true, BodyFixedMode = true, UseCache = true,
                        AerodynamicModel = "FAR", MaximumDecelerationG = 3.7, ImpactAvailable = true,
                        TimeToImpact = 83.2, ImpactLatitude = -.0972, ImpactLongitude = -74.5577,
                        ImpactSpeed = 178.4, TargetAvailable = true, TargetLatitude = -.1, TargetLongitude = -74.6,
                        EntryMode = "VELOCITY", EntryRetrograde = true, EntryAngle = 4.5
                    }
                });
                bridge.PublishPorkchop(new PorkchopResultSnapshot(4, 2, 2, 1000, 2000, 300, 900, 1, 0,
                    new[] { 4200.0, 3100.0, 5200.0, 4600.0 }));
                server.Start();
                await VerifyHttp(port, timeout.Token);
                await VerifyAuthentication(root, timeout.Token);
                await VerifyMultiClientPressure(root, timeout.Token);

                Uri socketUri = new Uri("ws://127.0.0.1:" + port + "/ws/realtime");
                await first.ConnectAsync(socketUri, timeout.Token);
                await second.ConnectAsync(socketUri, timeout.Token);
                Assert((await ReceiveType(first, "hello", timeout.Token)).GetProperty("protocol").GetInt32() == 1, "first client hello");
                Assert((await ReceiveType(second, "hello", timeout.Token)).GetProperty("protocol").GetInt32() == 1, "second client hello");
                JsonElement panelFirst = await ReceiveType(first, "telemetry.flightPanel", timeout.Token);
                JsonElement panelSecond = await ReceiveType(second, "telemetry.flightPanel", timeout.Token);
                Assert(panelFirst.GetProperty("partCount").GetInt32() == 184, "Flight Pannel complete snapshot");
                Assert(Math.Abs(panelSecond.GetProperty("angleOfSideslip").GetDouble() + .3) < .001, "Flight Pannel AoS field");
                JsonElement automationFirst = await ReceiveType(first, "telemetry.automation", timeout.Token);
                JsonElement automationSecond = await ReceiveType(second, "telemetry.automation", timeout.Token);
                Assert(automationFirst.GetProperty("maneuverNodeCount").GetInt32() == 2, "maneuver state snapshot");
                Assert(automationFirst.GetProperty("orbitGeometry").GetProperty("planned")[0][0].GetDouble() == 800000, "physical planned orbit serialization");
                Assert(automationFirst.GetProperty("trajectory").GetProperty("paths")[0][1][0].GetDouble() == 600000, "Trajectories patch point serialization");
                Assert(automationFirst.GetProperty("trajectory").GetProperty("groundPaths")[0][1][1].GetDouble() == 111, "geographic trajectory serialization");
                Assert(automationFirst.GetProperty("trajectory").GetProperty("impactTargetDistance").GetDouble() == 1234, "impact target distance serialization");
                Assert(Math.Abs(automationFirst.GetProperty("nextNodeDeltaV").GetDouble() - 312.4) < .001, "next maneuver delta-v readback");
                Assert(automationFirst.GetProperty("envelopeDynamicPressure").GetBoolean(), "MechJeb flight-envelope max-Q state");
                Assert(Math.Abs(automationFirst.GetProperty("envelopeMinimumThrottleValue").GetDouble() - .25) < .001, "MechJeb minimum throttle state");
                JsonElement trajectory = automationFirst.GetProperty("trajectory");
                Assert(trajectory.GetProperty("available").GetBoolean(), "Trajectories availability state");
                Assert(trajectory.GetProperty("aerodynamicModel").GetString() == "FAR", "Trajectories aerodynamic model state");
                Assert(trajectory.GetProperty("entryRetrograde").GetBoolean(), "Trajectories descent grade state");
                Assert(automationFirst.GetProperty("maneuverNodes").GetArrayLength() == 2, "complete maneuver queue readback");
                Assert(automationSecond.GetProperty("nextNodeUt").GetDouble() == 12345, "next maneuver time readback");
                Assert(automationSecond.GetProperty("smartAssMode").GetString() == "PROGRADE", "Smart ASS state snapshot");
                Assert(automationFirst.GetProperty("actionGroupMask").GetInt32() == 4, "action group readback snapshot");
                Assert(automationSecond.GetProperty("solarPanels").GetString() == "extended", "deployable readback snapshot");
                Assert(automationFirst.GetProperty("positionTargetExists").GetBoolean(), "surface target availability snapshot");
                Assert(Math.Abs(automationFirst.GetProperty("targetLongitude").GetDouble() + 74.5577) < .0001, "surface target coordinates snapshot");
                Assert(automationSecond.GetProperty("smartAssAutoDisable").GetBoolean(), "Smart ASS preference snapshot");
                Assert(automationFirst.GetProperty("smartAssSurfacePitch").GetDouble() == 30, "Smart ASS surface pitch readback");
                Assert(automationFirst.GetProperty("smartAssSurfaceYaw").GetDouble() == 90, "Smart ASS surface heading readback");
                Assert(automationFirst.GetProperty("smartAssSurfaceRoll").GetDouble() == 180, "Smart ASS surface roll readback");
                Assert(automationSecond.GetProperty("smartAssVelocityPitch").GetDouble() == 5, "Smart ASS velocity pitch readback");
                Assert(automationSecond.GetProperty("smartAssVelocityYaw").GetDouble() == 10, "Smart ASS velocity yaw readback");
                Assert(automationSecond.GetProperty("smartAssVelocityRoll").GetDouble() == 15, "Smart ASS velocity roll readback");
                Assert(automationSecond.GetProperty("smartAssRoll").GetDouble() == 45, "Smart ASS orbital roll readback");
                Assert(automationFirst.GetProperty("ascentLaunchMode").GetString() == "TARGET_PLANE", "MechJeb target-plane launch mode snapshot");
                Assert(automationSecond.GetProperty("ascentLaunchTargetAvailable").GetBoolean(), "MechJeb target launch readiness snapshot");
                Assert(automationFirst.GetProperty("dockingOverrideSafeDistance").GetBoolean(), "MechJeb docking safe-distance override snapshot");
                Assert(Math.Abs(automationFirst.GetProperty("dockingStartDistance").GetDouble() - 12.0) < .001, "MechJeb docking start-distance override snapshot");
                Assert(automationSecond.GetProperty("dockingForceRoll").GetBoolean(), "MechJeb docking force-roll snapshot");
                Assert(Math.Abs(automationSecond.GetProperty("dockingRoll").GetDouble() - 90) < .001, "MechJeb docking roll-angle snapshot");
                Assert(automationFirst.GetProperty("dockingAxisAvailable").GetBoolean(), "MechJeb docking-axis availability snapshot");
                Assert(Math.Abs(automationFirst.GetProperty("dockingAxisErrorY").GetDouble() + .134) < .001, "MechJeb docking-axis signed offset snapshot");
                Assert(automationFirst.GetProperty("landingPredictionOutcome").GetString() == "LANDED", "MechJeb landing prediction outcome snapshot");
                Assert(Math.Abs(automationFirst.GetProperty("landingPredictionTargetDistance").GetDouble() - 38.4) < .001, "MechJeb landing target-error snapshot");
                Assert(automationSecond.GetProperty("landingDeployGears").GetBoolean(), "MechJeb landing gear deployment setting snapshot");
                Assert(automationSecond.GetProperty("landingGearStageLimit").GetInt32() == 2, "MechJeb landing gear stage-limit snapshot");

                bridge.Publish(new TelemetrySnapshot(42, 1234.5, 3678.2, "FLIGHT", "vessel-1", "Test Vessel", 120000, 118500, 2300, 42, 91, 4, -2, .75, 1.2, 18.3));
                JsonElement firstContext = await ReceiveType(first, "event.context", timeout.Token);
                JsonElement secondContext = await ReceiveType(second, "event.context", timeout.Token);
                Assert(firstContext.GetProperty("vesselId").GetString() == "vessel-1", "first context event");
                Assert(secondContext.GetProperty("scene").GetString() == "FLIGHT", "second context event");
                JsonElement firstTelemetry = await ReceiveSequence(first, 42, timeout.Token);
                JsonElement secondTelemetry = await ReceiveSequence(second, 42, timeout.Token);
                Assert(firstTelemetry.GetProperty("vesselName").GetString() == "Test Vessel", "first telemetry payload");
                Assert(Math.Abs(firstTelemetry.GetProperty("met").GetDouble() - 3678.2) < .001, "mission time field");
                Assert(firstTelemetry.GetProperty("radarAltitude").GetDouble() == 118500, "radar altitude field");
                Assert(secondTelemetry.GetProperty("sequence").GetInt64() == 42, "second client broadcast");

                bridge.PublishRegular(new RegularTelemetrySnapshot(
                    7, 1234.5, "vessel-1", "Kerbin", "ORBITING",
                    120000, 85000, .03, 2.1, 1900, 430, 520, 682000,
                    -.09, -74.5, 2296, 7.2, 18.1, 84.2, 4, 184, 3, "Target Station"));
                JsonElement regularFirst = await ReceiveRegularSequence(first, 7, timeout.Token);
                JsonElement regularSecond = await ReceiveRegularSequence(second, 7, timeout.Token);
                Assert(regularFirst.GetProperty("periapsis").GetDouble() == 85000, "regular orbit payload");
                Assert(regularSecond.GetProperty("partCount").GetInt32() == 184, "regular multi-client broadcast");

                bridge.PublishRegular(new RegularTelemetrySnapshot(
                    8, 1234.7, "vessel-1", "Kerbin", "ORBITING",
                    120100, 85100, .03, 2.1, 1901, 429, 521, 682100,
                    -.09, -74.5, 2295, 7.19, 18.0, 83.8, 5, 181, 2, "Docking Port"));
                JsonElement vesselEventFirst = await ReceiveType(first, "event.vessel", timeout.Token);
                JsonElement vesselEventSecond = await ReceiveType(second, "event.vessel", timeout.Token);
                Assert(ArrayContains(vesselEventFirst.GetProperty("changes"), "stage"), "stage change event");
                Assert(ArrayContains(vesselEventFirst.GetProperty("changes"), "parts"), "part change event");
                Assert(ArrayContains(vesselEventSecond.GetProperty("changes"), "target"), "target change event");

                bridge.PublishRecorder(RecorderSample(2, 0.4, 2), false);
                JsonElement recorderFirst = await ReceiveRecorderSequence(first, 2, timeout.Token);
                JsonElement recorderSecond = await ReceiveRecorderSequence(second, 2, timeout.Token);
                Assert(recorderFirst.GetProperty("currentStage").GetInt32() == 2, "incremental recorder payload");
                Assert(Math.Abs(recorderSecond.GetProperty("angleOfAttack").GetDouble() - 2.4) < .001, "recorder MechJeb fields");
                long oldRevision = recorderFirst.GetProperty("revision").GetInt64();
                bridge.ResetRecorder();
                JsonElement resetFirst = await ReceiveType(first, "recorder.reset", timeout.Token);
                JsonElement resetSecond = await ReceiveType(second, "recorder.reset", timeout.Token);
                Assert(resetFirst.GetProperty("revision").GetInt64() > oldRevision, "recorder reset reaches first client without a sample");
                Assert(resetSecond.GetProperty("revision").GetInt64() == bridge.RecorderRevision, "recorder reset reaches second client");
                Assert(bridge.GetRecorderHistory().Samples.Length == 0, "recorder reset clears history");
                bridge.PublishRecorder(RecorderSample(3, 0, 2), false);
                JsonElement restartedFirst = await ReceiveRecorderSequence(first, 3, timeout.Token);
                JsonElement restartedSecond = await ReceiveRecorderSequence(second, 3, timeout.Token);
                Assert(restartedFirst.GetProperty("revision").GetInt64() == bridge.RecorderRevision, "new samples carry reset revision");
                Assert(restartedSecond.GetProperty("timeSinceMark").GetDouble() == 0, "both devices restart at T+0");
                Console.WriteLine("PASS recorder reset broadcast + epoch isolation for two clients");
                Console.WriteLine("PASS Smart ASS per-mode offset telemetry");

                await Send(first, "{\"type\":\"command\",\"commandId\":\"first\",\"name\":\"noop\",\"sequence\":1}", timeout.Token);
                await Task.Delay(40, timeout.Token);
                await Send(second, "{\"type\":\"command\",\"commandId\":\"second\",\"name\":\"noop\",\"sequence\":1}", timeout.Token);
                await DrainUntil(bridge, 2, timeout.Token);

                JsonElement firstAck = await ReceiveCommand(first, "first", timeout.Token);
                JsonElement secondAck = await ReceiveCommand(second, "second", timeout.Token);
                Assert(firstAck.GetProperty("success").GetBoolean(), "first command success");
                Assert(secondAck.GetProperty("success").GetBoolean(), "second command success");
                Assert(firstAck.GetProperty("serverSequence").GetInt64() < secondAck.GetProperty("serverSequence").GetInt64(), "global FIFO order");

                await Send(first, "{\"type\":\"command\",\"commandId\":\"smartass\",\"name\":\"mechjeb.smartass.set\",\"target\":\"PROGRADE\",\"sequence\":2}", timeout.Token);
                await DrainUntil(bridge, 1, timeout.Token);
                JsonElement smartAssAck = await ReceiveCommand(first, "smartass", timeout.Token);
                Assert(smartAssAck.GetProperty("success").GetBoolean(), "custom command handler acknowledgement");
                Assert(handledCommand != null && WireProtocol.ReadString(handledCommand.RawJson, "target") == "PROGRADE", "custom command parameters");

                await Send(first, "{\"type\":\"command\",\"commandId\":\"deduplicated\",\"name\":\"mechjeb.smartass.set\",\"target\":\"RETROGRADE\",\"sequence\":3}", timeout.Token);
                await Send(second, "{\"type\":\"command\",\"commandId\":\"deduplicated\",\"name\":\"mechjeb.smartass.set\",\"target\":\"RETROGRADE\",\"sequence\":2}", timeout.Token);
                await DrainUntil(bridge, 1, timeout.Token);
                JsonElement duplicateFirst = await ReceiveCommand(first, "deduplicated", timeout.Token);
                JsonElement duplicateSecond = await ReceiveCommand(second, "deduplicated", timeout.Token);
                Assert(duplicateFirst.GetProperty("serverSequence").GetInt64() == duplicateSecond.GetProperty("serverSequence").GetInt64(), "pending duplicate shares result");
                Assert(handledCommandCount == 2, "pending duplicate executes exactly once");
                await Send(first, "{\"type\":\"command\",\"commandId\":\"deduplicated\",\"name\":\"mechjeb.smartass.set\",\"target\":\"RETROGRADE\",\"sequence\":4}", timeout.Token);
                JsonElement replayed = await ReceiveCommand(first, "deduplicated", timeout.Token);
                Assert(replayed.GetProperty("serverSequence").GetInt64() == duplicateFirst.GetProperty("serverSequence").GetInt64(), "completed duplicate replays acknowledgement");
                Assert(handledCommandCount == 2, "completed duplicate is not re-executed");

                await Send(first, "{\"type\":\"ping\",\"sequence\":77}", timeout.Token);
                JsonElement pong = await ReceiveType(first, "pong", timeout.Token);
                Assert(pong.GetProperty("sequence").GetInt64() == 77, "application heartbeat");

                Console.WriteLine("PASS HTTP static + health");
                Console.WriteLine("PASS shared-token HTTP and WebSocket authentication");
                Console.WriteLine("PASS eight-client latest-only pressure isolation");
                Console.WriteLine("PASS RFC6455 handshake + two concurrent clients");
                Console.WriteLine("PASS latest telemetry broadcast");
                Console.WriteLine("PASS reliable scene/vessel context event");
                Console.WriteLine("PASS reliable stage/target/structure event");
                Console.WriteLine("PASS independent regular telemetry channel");
                Console.WriteLine("PASS MechJeb Flight Pannel snapshot channel");
                Console.WriteLine("PASS MechJeb planner/autopilot state channel");
                Console.WriteLine("PASS Flight Recorder history + incremental channel");
                Console.WriteLine("PASS global FIFO command acknowledgement");
                Console.WriteLine("PASS command idempotency for pending and completed duplicates");
                Console.WriteLine("PASS main-thread MechJeb command dispatch + parameters");
                Console.WriteLine("PASS application heartbeat");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL " + exception);
                return 1;
            }
            finally
            {
                server.Stop();
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }

    private static async Task VerifyHttp(int port, CancellationToken token)
    {
        using (var http = new HttpClient())
        {
            HttpResponseMessage indexResponse = await http.GetAsync("http://127.0.0.1:" + port + "/", token);
            string html = await indexResponse.Content.ReadAsStringAsync(token);
            string health = await http.GetStringAsync("http://127.0.0.1:" + port + "/health", token);
            string crewJson = await http.GetStringAsync("http://127.0.0.1:" + port + "/api/v1/crew", token);
            string vesselJson = await http.GetStringAsync("http://127.0.0.1:" + port + "/api/v1/vessel", token);
            string recorderJson = await http.GetStringAsync("http://127.0.0.1:" + port + "/api/v1/recorder", token);
            string recorderCsv = await http.GetStringAsync("http://127.0.0.1:" + port + "/api/v1/recorder.csv", token);
            string porkchopJson = await http.GetStringAsync("http://127.0.0.1:" + port + "/api/v1/porkchop", token);
            Assert(html.Contains("ArmorControl Test"), "static index response");
            Assert(indexResponse.Headers.Contains("Content-Security-Policy"), "static response security policy");
            Assert(indexResponse.Headers.Contains("X-Frame-Options"), "static response frame protection");
            using (JsonDocument document = JsonDocument.Parse(health))
            {
                Assert(document.RootElement.GetProperty("status").GetString() == "ok", "health response");
                JsonElement metrics = document.RootElement.GetProperty("metrics");
                Assert(metrics.GetProperty("fastSamples").GetInt64() == 90, "health capture metrics");
                Assert(Math.Abs(metrics.GetProperty("fastCaptureAverageMs").GetDouble() - .12) < .000001, "health capture average");
            }
            using (JsonDocument document = JsonDocument.Parse(crewJson))
            {
                JsonElement member = document.RootElement.GetProperty("crew")[0];
                Assert(member.GetProperty("name").GetString() == "Valentina Kerman", "current vessel crew endpoint");
                Assert(member.GetProperty("location").GetString() == "Command Pod", "crew location endpoint");
                Assert(member.GetProperty("evaAvailable").GetBoolean(), "crew EVA availability endpoint");
            }
            using (JsonDocument document = JsonDocument.Parse(vesselJson))
            {
                JsonElement parts = document.RootElement.GetProperty("parts");
                Assert(parts.GetArrayLength() == 2, "unbounded vessel parts endpoint");
                Assert(parts[1].GetProperty("parentId").GetUInt32() == 100, "part tree relationship");
                Assert(parts[1].GetProperty("resources")[0].GetProperty("name").GetString() == "LiquidFuel", "part resources endpoint");
                Assert(parts[0].GetProperty("engines")[0].GetProperty("moduleIndex").GetInt32() == 1, "individual engine identity endpoint");
                Assert(Math.Abs(parts[0].GetProperty("engines")[0].GetProperty("thrustLimit").GetDouble() - 72) < .001, "engine thrust limit endpoint");
                Assert(parts[0].GetProperty("toggles")[0].GetProperty("kind").GetString() == "rcs", "RCS remains a separate module endpoint");
                JsonElement aerodynamicCenter = document.RootElement.GetProperty("aerodynamicCenter");
                Assert(aerodynamicCenter.GetProperty("available").GetBoolean(), "FAR aerodynamic center availability endpoint");
                Assert(Math.Abs(aerodynamicCenter.GetProperty("normalizedX").GetDouble() - .44) < .001, "FAR aerodynamic center projection endpoint");
            }
            using (JsonDocument document = JsonDocument.Parse(recorderJson))
            {
                JsonElement samples = document.RootElement.GetProperty("samples");
                Assert(samples.GetArrayLength() == 1, "recorder initial history endpoint");
                Assert(Math.Abs(samples[0].GetProperty("timeSinceMark").GetDouble() - .2) < .001, "recorder history payload");
            }
            Assert(recorderCsv.StartsWith("TimeSinceMark,CurrentStage,AltitudeASL", StringComparison.Ordinal), "recorder CSV header");
            Assert(recorderCsv.Contains("0.2,3,1000.2", StringComparison.Ordinal), "recorder CSV sample");
            using (JsonDocument document = JsonDocument.Parse(porkchopJson))
            {
                Assert(document.RootElement.GetProperty("revision").GetInt64() == 4, "Porkchop revision endpoint");
                Assert(document.RootElement.GetProperty("costs").GetArrayLength() == 4, "Porkchop matrix endpoint");
                Assert(document.RootElement.GetProperty("bestDepartureIndex").GetInt32() == 1, "Porkchop best selection");
            }
        }
    }

    private static async Task VerifyAuthentication(string root, CancellationToken token)
    {
        int port = FindFreePort();
        using (var protectedServer = new ArmorControlServer(IPAddress.Loopback, port, root, new MainThreadBridge(), "secret token"))
        using (var http = new HttpClient())
        using (var socket = new ClientWebSocket())
        {
            protectedServer.Start();
            HttpResponseMessage denied = await http.GetAsync("http://127.0.0.1:" + port + "/api/v1/crew", token);
            Assert(denied.StatusCode == HttpStatusCode.Unauthorized, "protected API rejects missing token");
            HttpResponseMessage allowed = await http.GetAsync("http://127.0.0.1:" + port + "/api/v1/crew?token=secret%20token", token);
            Assert(allowed.StatusCode == HttpStatusCode.OK, "protected API accepts token");
            await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port + "/ws/realtime?token=secret%20token"), token);
            JsonElement hello = await ReceiveType(socket, "hello", token);
            Assert(hello.GetProperty("protocol").GetInt32() == 1, "protected WebSocket accepts token");
            protectedServer.Stop();
        }
    }

    private static async Task VerifyMultiClientPressure(string root, CancellationToken token)
    {
        int port = FindFreePort();
        var bridge = new MainThreadBridge();
        var sockets = new ClientWebSocket[8];
        using (var pressureServer = new ArmorControlServer(IPAddress.Loopback, port, root, bridge))
        {
            pressureServer.Start();
            try
            {
                for (int index = 0; index < sockets.Length; index++)
                {
                    sockets[index] = new ClientWebSocket();
                    await sockets[index].ConnectAsync(new Uri("ws://127.0.0.1:" + port + "/ws/realtime"), token);
                    await ReceiveType(sockets[index], "hello", token);
                }
                Assert(pressureServer.ClientCount == sockets.Length, "all pressure clients connected");
                for (int sequence = 1; sequence <= 2000; sequence++)
                    bridge.Publish(new TelemetrySnapshot(sequence, sequence, sequence, "FLIGHT", "pressure", "Pressure Vessel", sequence, 0, sequence, 0, 90, 0, 0, .5, 1, 2));
                for (int index = 0; index < 4; index++)
                {
                    JsonElement latest = await ReceiveSequence(sockets[index], 2000, token);
                    Assert(latest.GetProperty("sequence").GetInt64() == 2000, "active pressure client receives latest frame");
                }
                Assert(pressureServer.ClientCount == sockets.Length, "four unread clients do not disconnect or block active clients");
            }
            finally
            {
                foreach (ClientWebSocket socket in sockets) if (socket != null) socket.Dispose();
                pressureServer.Stop();
            }
        }
    }

    private static async Task DrainUntil(MainThreadBridge bridge, int expected, CancellationToken token)
    {
        int processed = 0;
        while (processed < expected)
        {
            token.ThrowIfCancellationRequested();
            processed += bridge.DrainCommands(expected - processed);
            if (processed < expected) await Task.Delay(10, token);
        }
    }

    private static async Task Send(ClientWebSocket socket, string text, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
    }

    private static async Task<JsonElement> ReceiveSequence(ClientWebSocket socket, long sequence, CancellationToken token)
    {
        while (true)
        {
            JsonElement message = await Receive(socket, token);
            if (message.GetProperty("type").GetString() == "telemetry.fast"
                && message.GetProperty("sequence").GetInt64() == sequence) return message;
        }
    }

    private static async Task<JsonElement> ReceiveRegularSequence(ClientWebSocket socket, long sequence, CancellationToken token)
    {
        while (true)
        {
            JsonElement message = await Receive(socket, token);
            if (message.GetProperty("type").GetString() == "telemetry.regular"
                && message.GetProperty("sequence").GetInt64() == sequence) return message;
        }
    }

    private static async Task<JsonElement> ReceiveRecorderSequence(ClientWebSocket socket, long sequence, CancellationToken token)
    {
        while (true)
        {
            JsonElement message = await Receive(socket, token);
            if (message.GetProperty("type").GetString() == "recorder.sample"
                && message.GetProperty("sequence").GetInt64() == sequence) return message;
        }
    }

    private static async Task<JsonElement> ReceiveType(ClientWebSocket socket, string type, CancellationToken token)
    {
        while (true)
        {
            JsonElement message = await Receive(socket, token);
            if (message.GetProperty("type").GetString() == type) return message;
        }
    }

    private static async Task<JsonElement> ReceiveCommand(ClientWebSocket socket, string commandId, CancellationToken token)
    {
        while (true)
        {
            JsonElement message = await Receive(socket, token);
            if (message.GetProperty("type").GetString() == "command.ack"
                && message.GetProperty("commandId").GetString() == commandId) return message;
        }
    }

    private static async Task<JsonElement> Receive(ClientWebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[65536];
        var content = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close) throw new EndOfStreamException("Server closed WebSocket.");
            content.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using (JsonDocument document = JsonDocument.Parse(content.ToArray()))
        {
            return document.RootElement.Clone();
        }
    }

    private static async Task TestServerLanguage(string root)
    {
        int port = FindFreePort();
        var bridge = new MainThreadBridge();
        var settings = new ArmorControlSettings { Language = "zh-CN" };
        string settingsPath = Path.Combine(root, "settings.cfg");
        using (var server = new ArmorControlServer(IPAddress.Loopback, port, root, bridge, language: settings.Language))
        using (var first = new ClientWebSocket())
        using (var second = new ClientWebSocket())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            bridge.CommandHandler = message =>
            {
                string error;
                if (!settings.TrySaveLanguage(WireProtocol.ReadString(message.RawJson, "language"), settingsPath, out error))
                    return new CommandExecution { Success = false, Message = error };
                server.SetLanguage(settings.Language);
                return new CommandExecution { Success = true, Message = "saved" };
            };
            server.Start();
            var uri = new Uri("ws://127.0.0.1:" + port + "/ws/realtime");
            await first.ConnectAsync(uri, timeout.Token);
            await second.ConnectAsync(uri, timeout.Token);
            await ReceiveType(first, "hello", timeout.Token);
            await ReceiveType(second, "hello", timeout.Token);
            Assert((await ReceiveType(first, "server.settings", timeout.Token)).GetProperty("language").GetString() == "zh-CN", "configured initial language");
            Assert((await ReceiveType(second, "server.settings", timeout.Token)).GetProperty("language").GetString() == "zh-CN", "same initial language on second device");
            await Send(first, "{\"type\":\"command\",\"commandId\":\"language-en\",\"name\":\"server.language.set\",\"language\":\"en-US\"}", timeout.Token);
            await DrainUntil(bridge, 1, timeout.Token);
            Assert(File.ReadAllText(settingsPath).Contains("language = en-US"), "web command saves configuration before broadcast");
            Assert((await ReceiveType(first, "server.settings", timeout.Token)).GetProperty("language").GetString() == "en-US", "live language change on first device");
            Assert((await ReceiveType(second, "server.settings", timeout.Token)).GetProperty("language").GetString() == "en-US", "live language change on second device");
            bool rejected = false;
            try { server.SetLanguage("invalid"); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "invalid language rejected");
            second.Abort();
            using (var reconnected = new ClientWebSocket())
            {
                await reconnected.ConnectAsync(uri, timeout.Token);
                await ReceiveType(reconnected, "hello", timeout.Token);
                Assert((await ReceiveType(reconnected, "server.settings", timeout.Token)).GetProperty("language").GetString() == "en-US", "reconnected device follows current server setting");
                await Send(reconnected, "{\"type\":\"command\",\"commandId\":\"language-zh\",\"name\":\"server.language.set\",\"language\":\"zh-CN\"}", timeout.Token);
                await DrainUntil(bridge, 1, timeout.Token);
                Assert(File.ReadAllText(settingsPath).Contains("language = zh-CN"), "second web device saves global setting");
                Assert((await ReceiveType(first, "server.settings", timeout.Token)).GetProperty("language").GetString() == "zh-CN", "switch back first");
                Assert((await ReceiveType(reconnected, "server.settings", timeout.Token)).GetProperty("language").GetString() == "zh-CN", "switch back reconnected");
                settingsPath = root; // A directory cannot be replaced with settings.cfg content.
                await Send(first, "{\"type\":\"command\",\"commandId\":\"language-failed\",\"name\":\"server.language.set\",\"language\":\"en-US\"}", timeout.Token);
                await DrainUntil(bridge, 1, timeout.Token);
                Assert(!(await ReceiveCommand(first, "language-failed", timeout.Token)).GetProperty("success").GetBoolean(), "write failure acknowledged");
                Assert(settings.Language == "zh-CN", "failed web save preserves global setting");
                using (var afterFailure = new ClientWebSocket())
                {
                    await afterFailure.ConnectAsync(uri, timeout.Token);
                    Assert((await ReceiveType(afterFailure, "server.settings", timeout.Token)).GetProperty("language").GetString() == "zh-CN", "failed save not broadcast to new devices");
                }
            }
        }
        Console.WriteLine("PASS server language: configured startup, two-device live sync, reconnect and invalid-value rejection without telemetry");
    }

    private static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static bool ArrayContains(JsonElement array, string expected)
    {
        foreach (JsonElement value in array.EnumerateArray())
        {
            if (value.GetString() == expected) return true;
        }
        return false;
    }

    private static FlightRecorderSample RecorderSample(long sequence, double time, int stage)
    {
        return new FlightRecorderSample(sequence, time, stage, 1000 + time, 50, 200, 210, 12,
            1.1, 4.2, 2.4, -.3, 2.5, 900, 87, 12, 7, 3, 24);
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
    }
}
