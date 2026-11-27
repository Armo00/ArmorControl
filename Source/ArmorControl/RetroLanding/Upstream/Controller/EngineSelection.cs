using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Group = LandingPoc.EngineGroup;

namespace LandingPoc
{
    public sealed partial class Controller
    {
        private const double NearIgnitionSeconds = 10;
        private const int NeighborLimit = 8;
        private const int QuickGroupCount = 6;
        private const int WorkerBatchSize = 8;

        private readonly bool backgroundCatalog;
        private readonly HashSet<long> priorityMasks;
        private readonly HashSet<long> cachedMasks = new HashSet<long>();
        private readonly Dictionary<long, Group> liveByMask = new Dictionary<long, Group>();
        private readonly List<Group> liveGroups = new List<Group>();
        private readonly List<Group> terminalGroups = new List<Group>();
        private EngineGroups catalogBuilder;
        private bool groupsDirty, catalogComplete;
        private SearchWork catalogSearch;
        private Engine[] completedMeasurements;
        private long neighborOf;
        private readonly List<long> moreEngines = new List<long>();
        private readonly List<long> fewerEngines = new List<long>();
        private readonly List<long> substituteEngines = new List<long>();

        private sealed class SearchWork
        {
            internal readonly CancellationTokenSource Stop = new CancellationTokenSource();
            internal readonly BlockingCollection<long> Masks = new BlockingCollection<long>(2 * WorkerBatchSize);
            internal Engine[] Measurements;
            internal Task Task;
        }

        private List<Group> groups
        {
            get
            {
                if (!groupsDirty) return liveGroups;
                liveGroups.Clear();
                var masks = new HashSet<long>(cachedMasks) { catalogAvailable, catalogRunning };
                foreach (long family in editorMasks) masks.Add(family & catalogAvailable);
                foreach (long mask in masks)
                {
                    if (!Subset(mask, catalogAvailable)) continue;
                    Group group = LiveGroup(mask);
                    if (group != null) liveGroups.Add(group);
                }
                liveGroups.Sort((a, b) => a.Mask.CompareTo(b.Mask));
                terminalGroups.Clear();
                terminalGroups.AddRange(liveGroups);
                terminalGroups.Sort(CompareCandidateGroups);
                groupsDirty = false;
                return liveGroups;
            }
        }

        private static int CompareCandidateGroups(Group a, Group b)
        {
            int order = CompareGroups(a, b);
            return order == 0 ? a.Mask.CompareTo(b.Mask) : order;
        }

        private Group LiveGroup(long mask)
        {
            if (mask == 0) return null;
            if (!liveByMask.TryGetValue(mask, out Group group))
            {
                group = catalogBuilder.MakeGroup(mask);
                if (!group.Evaluate(backupTorque)) group = null;
                liveByMask.Add(mask, group);
            }
            return group;
        }

        private IEnumerable<Group> PlanGroups(long mask, List<Group> choices, bool terminal = false)
        {
            if (mask != 0)
            {
                Group group = LiveGroup(mask);
                if (group != null) yield return group;
                yield break;
            }
            List<Group> all = choices ?? groups;
            foreach (Group group in choices == null && terminal ? terminalGroups : all)
                yield return group;
        }

        private void Remember(long mask)
        {
            groupsDirty |= cachedMasks.Add(mask);
        }

        private void UpdateGroupAvailability(Flight s)
        {
            long available = s.Running | s.Starting | s.Restartable;
            if (catalogAvailable != available)
            {
                catalogAvailable = available;
                catalogComplete = false;
                CancelCatalogSearch();
                groupsDirty = true;
            }
            if (catalogRunning != s.Running)
            {
                catalogRunning = s.Running;
                groupsDirty = true;
            }
        }

        private void CompleteCatalog()
        {
            if (catalogComplete || footprintSimulation) return;
            CancelCatalogSearch();
            if (specs.Length <= EngineModuleCatalog.ExhaustiveLimit)
            {
                for (long mask = catalogAvailable; mask != 0; mask = (mask - 1) & catalogAvailable)
                    Remember(mask);
            }
            else
            {
                var candidates = new List<Group>();
                catalogBuilder.Populate(candidates, catalogAvailable, catalogRunning, editorMasks, selected, emergencyGroup);
                foreach (Group candidate in candidates) Remember(candidate.Mask);
            }
            catalogComplete = true;
            completedMeasurements = specs;
        }

        private void StartCatalogSearch()
        {
            if (!backgroundCatalog || catalogSearch != null || catalogComplete) return;
            var work = new SearchWork { Measurements = specs };
            var published = new HashSet<long>(cachedMasks)
            {
                catalogAvailable, catalogRunning
            };
            long available = catalogAvailable;
            long running = catalogRunning;
            CancellationToken token = work.Stop.Token;
            Engine[] snapshot = (Engine[])specs.Clone();
            EngineGroups builder = catalogBuilder.WithMeasurements(snapshot, backupTorque, token, mask =>
            {
                if (published.Add(mask)) work.Masks.Add(mask, token);
            });
            // The worker only sees immutable measurements and installation geometry.
            long preferred = selected;
            work.Task = Task.Run(() => builder.Populate(new List<Group>(), available, running, editorMasks, preferred), token);
            catalogSearch = work;
        }

        private List<long> AcceptCatalogBatch()
        {
            SearchWork work = catalogSearch;
            if (work == null) return null;
            List<long> added = null;
            for (int i = 0; i < WorkerBatchSize && work.Masks.TryTake(out long mask); i++)
            {
                Remember(mask);
                if (LiveGroup(mask) == null) continue;
                if (added == null) added = new List<long>(WorkerBatchSize);
                added.Add(mask);
            }
            if (work.Task.IsCompleted && work.Masks.Count == 0)
            {
                catalogComplete = work.Task.Status == TaskStatus.RanToCompletion;
                if (catalogComplete) completedMeasurements = work.Measurements;
                CancelCatalogSearch();
            }
            return added;
        }

        internal void CancelCatalogSearch()
        {
            SearchWork work = catalogSearch;
            if (work == null) return;
            catalogSearch = null;
            work.Stop.Cancel();
            work.Task.ContinueWith(completed =>
            {
                _ = completed.Exception;
                work.Masks.Dispose();
                work.Stop.Dispose();
            }, TaskScheduler.Default);
        }

        internal bool TryFallbackBoostbackBanks(Flight flight, out long main, out long precision, out long landing)
        {
            main = precision = landing = 0;
            UpdateGroupAvailability(flight);
            StartCatalogSearch();
            AcceptCatalogBatch();
            List<Group> candidates = groups;
            double best = 0;
            long finalStarts = 0;
            for (int i = 0; i < specs.Length; i++)
                if (specs[i].Ignitions == 1) finalStarts |= 1L << i;
            foreach (Group bank in candidates)
            {
                if (!TryBoostbackLimits(bank.Mask, flight, out _, out double maximum) || maximum < best) continue;
                if (maximum == best && main != 0 && CompareGroups(bank, LiveGroup(main)) >= 0) continue;
                Group inner = null, reserve = null;
                foreach (Group option in terminalGroups)
                {
                    if (inner == null && Subset(option.Mask, bank.Mask) && TryBoostbackLimits(option.Mask, flight, out _, out _))
                        inner = option;
                    if (reserve == null && (option.Mask & bank.Mask & finalStarts) == 0 &&
                        (option.Mask & fixedEngines) == 0 && Eligible(option, flight) &&
                        Limit(option, flight, true) > flight.Mass * cfg.Gravity)
                        reserve = option;
                    if (inner != null && reserve != null) break;
                }
                if (inner == null || reserve == null) continue;
                main = bank.Mask;
                precision = inner.Mask;
                landing = reserve.Mask;
                best = maximum;
            }
            return main != 0;
        }

        private void PrepareNeighbors(long active)
        {
            if (active == 0 || active == neighborOf) return;
            neighborOf = active;
            var masks = new HashSet<long>(priorityMasks);
            masks.UnionWith(cachedMasks);
            foreach (long geometry in priorityMasks)
            {
                masks.Add(active | geometry);
                masks.Add(active & ~geometry);
            }
            var order = Comparer<Group>.Create(CompareCandidateGroups);
            Group[] candidates = masks.Where(mask => mask != active && Subset(mask, catalogAvailable))
                .Select(LiveGroup).Where(group => group != null).ToArray();

            moreEngines.Clear();
            moreEngines.AddRange(candidates.Where(g => Subset(active, g.Mask)).OrderBy(g => g.Count)
                .ThenBy(g => g, order).Take(NeighborLimit).Select(g => g.Mask));
            fewerEngines.Clear();
            fewerEngines.AddRange(candidates.Where(g => Subset(g.Mask, active)).OrderBy(g => g, order)
                .Take(NeighborLimit).Select(g => g.Mask));
            substituteEngines.Clear();
            substituteEngines.AddRange(candidates.Where(g => !Subset(g.Mask, active) && !Subset(active, g.Mask))
                .OrderBy(g => ChangedEngines(g.Mask ^ active)).ThenBy(g => g, order)
                .Take(NeighborLimit).Select(g => g.Mask));
        }

        private static int ChangedEngines(long mask)
        {
            int count = 0;
            for (; mask != 0; mask &= mask - 1) count++;
            return count;
        }

        private IEnumerable<Group> TryFirst(long preferred)
        {
            Group group = LiveGroup(preferred);
            if (group != null) yield return group;
            foreach (Group alternative in groups)
                if (alternative.Mask != preferred) yield return alternative;
        }

        private IEnumerable<Group> TryFirst(List<long> preferred)
        {
            if (preferred != null)
            {
                foreach (long mask in preferred)
                {
                    Group cached = LiveGroup(mask);
                    if (cached != null) yield return cached;
                }
                CompleteCatalog();
            }
            foreach (Group candidate in groups)
                if (preferred == null || !preferred.Contains(candidate.Mask)) yield return candidate;
        }

        internal bool CoastIgnitionImminent(double now)
        {
            if (phase != Phase.Coast || !coastSearch.HasValue) return false;
            CoastSearch saved = coastSearch.Value;
            return saved.Plan.Main != null && !cfg.IgnitionAltitudeBlocked(saved.Flight.Height) &&
                saved.Plan.Wait - (now - saved.Flight.UniversalTime.Value) <= 2.5 * cfg.SampleSeconds;
        }

        private Plan FindCoastPlan(Flight s, bool ignitionBlocked)
        {
            List<long> newCandidates = AcceptCatalogBatch();
            if (coastSearch.HasValue && SameCoastSettings(coastSearch.Value) && s.UniversalTime.HasValue &&
                newCandidates == null && (s.Running | s.Starting | s.IssuedStarts) == 0)
            {
                CoastSearch saved = coastSearch.Value;
                Flight before = saved.Flight;
                double elapsed = s.UniversalTime.Value - before.UniversalTime.Value;
                double wait = saved.Plan.Wait - elapsed;
                double interval = saved.Plan.Main == null || wait <= NearIgnitionSeconds ? 0.5 : 1;
                bool atIgnition = !ignitionBlocked && saved.Plan.Main != null && wait <= cfg.SampleSeconds * 1.5;
                if (elapsed >= 0 && elapsed < interval && !atIgnition &&
                    cfg.IgnitionAltitudeBlocked(before.Height) == ignitionBlocked &&
                    before.Restartable == s.Restartable && before.Unsettled == s.Unsettled &&
                    (before.Vz < 0) == (s.Vz < 0))
                {
                    Plan retained = saved.Plan;
                    retained.Wait = Math.Max(0, wait);
                    cappedPlanMargin = saved.Margin;
                    propellantShortfall = saved.Shortfall;
                    return retained;
                }
            }

            Plan next = plan.Main == null ? default : RecheckCoastPair(s, plan);
            if (next.Main == null && plan.Main != null)
            {
                next = FindCachedCoastPair(s, plan);
                if (specs.Length > EngineModuleCatalog.ExhaustiveLimit && completedMeasurements != specs)
                    catalogComplete = false;
            }
            if (next.Main == null)
            {
                if (!backgroundCatalog || catalogComplete)
                {
                    CompleteCatalog();
                    next = FindPlan(s);
                }
                else
                {
                    StartCatalogSearch();
                    List<Group> quick = QuickCoastGroups(s, newCandidates);
                    next = FindPlan(s, choices: quick);
                    // Preserve the last safe decision window even if the worker is delayed.
                    if (next.Main == null && !ignitionBlocked && !MayWaitForCatalog(s))
                    {
                        CompleteCatalog();
                        next = FindPlan(s);
                    }
                }
            }
            if (ignitionBlocked && next.Main == null && catalogComplete) FindLaterAeroLanding(s, ref next);

            if (next.Main != null)
            {
                Remember(next.Main.Mask);
                Remember(next.Final.Mask);
                PrepareNeighbors(next.Main.Mask);
                CancelCatalogSearch();
            }
            RecordCoastCheck(s, next);
            return next;
        }

        private void RecordCoastCheck(Flight s, Plan checkedPlan)
        {
            coastSearch = s.UniversalTime.HasValue ? new CoastSearch {
                Flight = s, Plan = checkedPlan, MaxG = cfg.MaxG, Altitude = cfg.MaxIgnitionAltitude,
                Precision = cfg.PrecisionSeconds, Optimize = cfg.OptimizeFuel, Margin = cappedPlanMargin,
                Shortfall = propellantShortfall
            } : (CoastSearch?)null;
        }

        private bool MayWaitForCatalog(Flight s)
        {
            // Use the best theoretical capped deceleration as a deadline,
            // then leave time for a measured start and a terminal handoff.
            double spool = 0;
            for (int i = 0; i < specs.Length; i++)
                if ((s.Restartable & (1L << i)) != 0 && EngineResponse.Known(specs[i].SpoolSeconds))
                    spool = Math.Max(spool, specs[i].SpoolSeconds);
            double delay = Math.Max(cfg.PrecisionSeconds, cfg.AttitudeResponseSeconds);
            return CoastMargin(s, delay + spool + s.AlignmentSeconds + s.UllageSeconds,
                cfg.MaxG * Settings.G0, 0, cfg.TouchSpeed) > 0;
        }

        private List<Group> QuickCoastGroups(Flight s, List<long> discovered)
        {
            var order = Comparer<Group>.Create(CompareCandidateGroups);
            List<Group> viable = groups.Where(group => Eligible(group, s) &&
                group.Min <= PlanningLimit(group, s, Settings.InitialPlanningThrottle) &&
                PlanningLimit(group, s, Settings.InitialPlanningThrottle) > s.Mass * cfg.Gravity).ToList();
            var final = viable.OrderBy(g => g, order).Take(QuickGroupCount);
            var main = viable.OrderByDescending(g => PlanningLimit(g, s, Settings.InitialPlanningThrottle))
                .ThenBy(g => !g.CenteredInstallation).ThenBy(g => g, order).Take(QuickGroupCount);
            IEnumerable<Group> shortlist = final.Concat(main);
            if (discovered != null)
                shortlist = shortlist.Concat(discovered.Select(LiveGroup).Where(group => group != null && Eligible(group, s)));
            return shortlist.Distinct().OrderBy(g => g, order).ToList();
        }

        private Plan RecheckCoastPair(Flight s, Plan prior)
        {
            Plan next = ReplanCoastPair(s, prior);
            Group main = LiveGroup(prior.Main.Mask);
            Group final = LiveGroup(prior.Final.Mask);
            if (next.Main != null || main == null || final == null || !Eligible(main, s) || !Eligible(final, s))
                return next;
            // The same bank may still fly a different handoff when the
            // original terminal duration or throttle has become infeasible.
            foreach (double factor in TerminalDurationFactors)
            {
                Plan recipe = prior;
                recipe.RequestedTerminalSeconds = cfg.PrecisionSeconds;
                recipe.TerminalSeconds = cfg.PrecisionSeconds * factor;
                if (recipe.TerminalSeconds == prior.TerminalSeconds) continue;
                next = ReplanCoastPair(s, recipe);
                if (next.Main != null) return next;
            }
            return next;
        }

        private Plan FindCachedCoastPair(Flight s, Plan prior)
        {
            PrepareNeighbors(prior.Main.Mask);
            foreach (long mask in substituteEngines.Concat(moreEngines).Concat(fewerEngines))
            {
                Group bank = LiveGroup(mask);
                if (bank == null || !Eligible(bank, s)) continue;
                Plan next = prior;
                next.Main = bank;
                if (!Subset(next.Final.Mask, mask))
                {
                    next.Final = groups.FirstOrDefault(group => group.Count == prior.Final.Count &&
                        Subset(group.Mask, mask) && Eligible(group, s));
                    if (next.Final == null) continue;
                }
                next = ReplanCoastPair(s, next);
                if (next.Main != null) return next;
            }
            return default;
        }
    }
}
