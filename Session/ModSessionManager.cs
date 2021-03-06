﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Xml.Serialization;
using Equinox.Utils.Collections;
using Equinox.Utils.Logging;
using ProtoBuf;
using VRage.Collections;
using VRage.Game;
using VRage.Utils;

namespace Equinox.Utils.Session
{
    public class ModSessionManager
    {
        private class RuntimeSessionComponent
        {
            public ModSessionComponent Component { get; private set; }
            public Ob_ModSessionComponent Config { get; private set; }

            public readonly HashSet<RuntimeSessionComponent> Dependents = new HashSet<RuntimeSessionComponent>();
            public readonly HashSet<RuntimeSessionComponent> Dependencies = new HashSet<RuntimeSessionComponent>();

            /// <summary>
            /// Only used when sorting the components in the DAG.  This is the in-factor
            /// </summary>
            public int UnsolvedDependencies, UnsolvedDependenciesNext;

            public MovingAverageInt64 UpdateBeforeSimulationTime;
            public MovingAverageInt64 UpdateBeforeSimulationRRTime, UpdateBeforeSimulationRRJobs;
            public MovingAverageInt64 UpdateAfterSimulationTime;
            public MovingAverageInt64 UpdateAfterSimulationRRTime, UpdateAfterSimulationRRJobs;

            private static void EnsureLength(ref MovingAverageInt64 field, int length)
            {
                if (length <= 0)
                {
                    field = null;
                    return;
                }
                if (field == null)
                    field = new MovingAverageInt64(length);
                else
                    field.Resize(length);
            }

            public void SetProfilingAverageLength(int length)
            {
                EnsureLength(ref UpdateBeforeSimulationTime, length);
                EnsureLength(ref UpdateBeforeSimulationRRTime, length);
                EnsureLength(ref UpdateBeforeSimulationRRJobs, length);
                EnsureLength(ref UpdateAfterSimulationTime, length);
                EnsureLength(ref UpdateAfterSimulationRRTime, length);
                EnsureLength(ref UpdateAfterSimulationRRJobs, length);
            }

            public RuntimeSessionComponent(ModSessionComponent c, Ob_ModSessionComponent cfg)
            {
                Component = c;
                Config = cfg;
            }
        }

        private struct RemoveOrAdd
        {
            public readonly bool Remove;
            public readonly ModSessionComponent Component;
            public readonly Ob_ModSessionComponent Config;

            public RemoveOrAdd(ModSessionComponent c, bool r, Ob_ModSessionComponent cfg = null)
            {
                Component = c;
                Remove = r;
                Config = cfg;
            }
        }

        public event Action<ModSessionComponent> ComponentAttached, ComponentDetached;

        private readonly Dictionary<Type, List<RuntimeSessionComponent>> m_componentDictionary = new Dictionary<Type, List<RuntimeSessionComponent>>();
        private readonly Dictionary<Type, RuntimeSessionComponent> m_dependencySatisfyingComponents = new Dictionary<Type, RuntimeSessionComponent>();
        private readonly List<RuntimeSessionComponent> m_orderedComponentList = new List<RuntimeSessionComponent>();
        private readonly MyConcurrentQueue<RemoveOrAdd> m_componentsToModify = new MyConcurrentQueue<RemoveOrAdd>();
        private bool m_attached = false;

        public ILoggingBase FallbackLogger { get; }
        public IEnumerable<ModSessionComponent> OrderedComponents => m_orderedComponentList.Select(x => x.Component);
        public TimeSpan TolerableLag { get; set; } = TimeSpan.FromSeconds(MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS);

        private int m_profilingAverageLength = 0;
        public int ProfilingAverageLength
        {
            get { return m_profilingAverageLength; }
            set
            {
                if (m_profilingAverageLength == value) return;
                m_profilingAverageLength = value;
                foreach (var x in m_componentDictionary.Values.SelectMany(x => x))
                    x.SetProfilingAverageLength(value);
            }
        }

        public ModSessionManager()
        {
            FallbackLogger = new FallbackLogger(this);
        }

        private void Insert(RuntimeSessionComponent rsc)
        {
            rsc.SetProfilingAverageLength(ProfilingAverageLength);
            var myType = rsc.Component.GetType();
            List<RuntimeSessionComponent> list;
            if (!m_componentDictionary.TryGetValue(myType, out list))
                m_componentDictionary[myType] = list = new List<RuntimeSessionComponent>();
            list.Add(rsc);
            foreach (var dep in rsc.Component.SuppliedComponents)
                m_dependencySatisfyingComponents.Add(dep, rsc);
        }

        private void Remove(ModSessionComponent component)
        {
            var myType = component.GetType();
            List<RuntimeSessionComponent> list;
            if (m_componentDictionary.TryGetValue(myType, out list))
            {
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i].Component != component) continue;
                    if (list[i].Dependents.Count > 0)
                    {
                        FallbackLogger.Log(MyLogSeverity.Critical, "Unable to remove {0} because it still has {1} dependents", component.GetType(), list[i].Dependents.Count);
                        using (FallbackLogger.IndentUsing())
                            foreach (var dep in list[i].Dependents)
                                FallbackLogger.Log(MyLogSeverity.Critical, "Dependent: {0}", dep.Component.GetType());
                        throw new ArgumentException("Can't remove " + component.GetType() + " because it still has dependents");
                    }
                    foreach (var dep in list[i].Dependencies)
                        dep.Dependents.Remove(list[i]);
                    list.RemoveAtFast(i);
                    break;
                }
                if (list.Count == 0)
                    m_componentDictionary.Remove(myType);
            }
            foreach (var dep in component.SuppliedComponents)
                m_dependencySatisfyingComponents.Remove(dep);
            for (var i = 0; i < m_orderedComponentList.Count; i++)
                if (m_orderedComponentList[i].Component == component)
                {
                    m_orderedComponentList.RemoveAt(i);
                    break;
                }
            component.Detached();
            ComponentDetached?.Invoke(component);
        }

        public void Unregister(ModSessionComponent component)
        {
            if (!m_attached)
            {
                Remove(component);
                return;
            }
            m_componentsToModify.Enqueue(new RemoveOrAdd(component, true));
        }

        public void Register(ModSessionComponent component, Ob_ModSessionComponent config = null)
        {
            foreach (var sat in component.SuppliedComponents)
                if (m_dependencySatisfyingComponents.ContainsKey(sat))
                    throw new ArgumentException("We already have a component satisfying the " + sat + " dependency; we can't have two.");

            if (!m_attached)
            {
                var rsc = new RuntimeSessionComponent(component, config);
                Insert(rsc);
                return;
            }
            m_componentsToModify.Enqueue(new RemoveOrAdd(component, false, config));
        }

        private readonly Dictionary<Type, ModSessionComponentRegistry.CreateComponent> _factories = new Dictionary<Type, ModSessionComponentRegistry.CreateComponent>();

        public void RegisterFactory(IEnumerable<Type> suppliers, ModSessionComponentRegistry.CreateComponent component)
        {
            foreach (var k in suppliers)
                _factories.Add(k, component);
        }

        public bool UnregisterFactory(ModSessionComponentRegistry.CreateComponent component)
        {
            var supplies = new HashSet<Type>();
            foreach (var kv in _factories)
                if (kv.Value == component)
                    supplies.Add(kv.Key);
            if (supplies.Count == 0)
                return false;
            foreach (var key in supplies)
                _factories.Remove(key);
            return true;
        }

        private void AddComponentLazy(ModSessionComponent component, Ob_ModSessionComponent config)
        {
            foreach (var dep in component.Dependencies)
            {
                if (m_dependencySatisfyingComponents.ContainsKey(dep))
                    continue;
                ModSessionComponentRegistry.CreateComponent factory;
                if (!_factories.TryGetValue(dep, out factory))
                    throw new ArgumentException("Can't add " + component.GetType() +
                                                    " since we don't have dependency " + dep + " loaded or a factory for it");
                AddComponentLazy(factory(), null);
            }
            // Safe to add to the end of the ordered list.
            var item = new RuntimeSessionComponent(component, config);
            foreach (var dep in component.Dependencies)
            {
                var depRes = m_dependencySatisfyingComponents[dep];
                depRes.Dependents.Add(item);
                item.Dependencies.Add(item);
                item.Component.SatisfyDependency(depRes.Component);
            }
            Insert(item);
            m_orderedComponentList.Add(item);
            if (item.Config != null)
                item.Component.LoadConfiguration(item.Config);
            item.Component.Attached(this);
            ComponentAttached?.Invoke(item.Component);
        }

        private void ApplyComponentChanges()
        {
            RemoveOrAdd info;
            while (m_componentsToModify.TryDequeue(out info))
            {
                var component = info.Component;
                if (info.Remove)
                    Remove(component);
                else
                    AddComponentLazy(component, info.Config);
            }
        }

        public IEnumerable<T> GetAll<T>() where T : ModSessionComponent
        {
            return m_componentDictionary.GetValueOrDefault(typeof(T), null)?.Cast<T>() ?? Enumerable.Empty<T>();
        }

        public T GetDependencyProvider<T>() where T : ModSessionComponent
        {
            return m_dependencySatisfyingComponents.GetValueOrDefault(typeof(T), null)?.Component as T;
        }

        private readonly List<RuntimeSessionComponent> m_dagQueue = new List<RuntimeSessionComponent>();
        private readonly List<RuntimeSessionComponent> m_tmpQueue = new List<RuntimeSessionComponent>();
        private void SortComponents()
        {
            ApplyComponentChanges();
            // Fill using factories
            var prevCount = 0;
            while (true)
            {
                var entries = m_componentDictionary.Values.SelectMany(x => x).ToList();
                if (entries.Count == prevCount)
                    break;

                foreach (var sat in entries)
                    foreach (var dep in sat.Component.Dependencies)
                    {
                        if (m_dependencySatisfyingComponents.ContainsKey(dep))
                            continue;
                        ModSessionComponentRegistry.CreateComponent factory;
                        if (!_factories.TryGetValue(dep, out factory))
                            throw new ArgumentException("Unable to resolve " + dep + " for " + sat.Component.GetType());
                        Register(factory());
                    }
                prevCount = entries.Count;
            }
            // Fill dependency information.
            foreach (var c in m_componentDictionary.Values.SelectMany(x => x))
            {
                c.Dependencies.Clear();
                c.Dependents.Clear();
            }
            foreach (var c in m_componentDictionary.Values.SelectMany(x => x))
                foreach (var dependency in c.Component.Dependencies)
                {
                    RuntimeSessionComponent resolved;
                    if (m_dependencySatisfyingComponents.TryGetValue(dependency, out resolved))
                    {
                        resolved.Dependents.Add(c);
                        c.Dependencies.Add(resolved);
                        c.Component.SatisfyDependency(resolved.Component);
                        c.UnsolvedDependencies = c.Dependencies.Count;
                    }
                    else
                        throw new ArgumentException("Unable to resolve " + dependency + " for " + c.Component.GetType());
                }

            m_orderedComponentList.Clear();
            m_dagQueue.AddRange(m_componentDictionary.Values.SelectMany(x => x));
            while (m_dagQueue.Count > 0)
            {
                foreach (var x in m_componentDictionary.Values.SelectMany(y => y))
                    x.UnsolvedDependenciesNext = x.UnsolvedDependencies;

                m_tmpQueue.Clear();
                for (var i = 0; i < m_dagQueue.Count; i++)
                {
                    var c = m_dagQueue[i];
                    if (c.UnsolvedDependencies == 0)
                    {
                        m_tmpQueue.Add(c);
                        foreach (var d in c.Dependents)
                            d.UnsolvedDependenciesNext--;
                    }
                    else if (m_tmpQueue.Count > 0)
                        m_dagQueue[i - m_tmpQueue.Count] = c;
                }
                foreach (var x in m_componentDictionary.Values.SelectMany(y => y))
                    x.UnsolvedDependencies = x.UnsolvedDependenciesNext;
                if (m_tmpQueue.Count == 0)
                {
                    FallbackLogger.Log(MyLogSeverity.Critical, "Dependency loop detected when solving session DAG");
                    using (FallbackLogger.IndentUsing())
                        foreach (var k in m_dagQueue)
                            FallbackLogger.Log(MyLogSeverity.Critical, "{0}x{1} has {2} unsolved dependencies.  Dependencies are {3}, Dependents are {4}", k.Component.GetType().Name,
                                k.Component.GetType().GetHashCode(), k.UnsolvedDependencies,
                                string.Join(", ", k.Dependencies.Select(x => x.Component.GetType() + "x" + x.Component.GetHashCode())),
                                string.Join(", ", k.Dependents.Select(x => x.Component.GetType() + "x" + x.Component.GetHashCode())));
                    throw new ArgumentException("Dependency loop inside " + string.Join(", ", m_dagQueue.Select(x => x.Component.GetType() + "x" + x.Component.GetHashCode())));
                }
                m_dagQueue.RemoveRange(m_dagQueue.Count - m_tmpQueue.Count, m_tmpQueue.Count);
                // Sort temp queue, add to sorted list.
                m_tmpQueue.Sort((a, b) => a.Component.Priority.CompareTo(b.Component.Priority));
                foreach (var k in m_tmpQueue)
                    m_orderedComponentList.Add(k);
                m_tmpQueue.Clear();
            }
        }

        public void Attach()
        {
            if (m_attached) return;
            SortComponents();
            m_attached = true;
            foreach (var x in m_orderedComponentList)
            {
                if (x.Config != null)
                    x.Component.LoadConfiguration(x.Config);
                x.Component.Attached(this);
                ComponentAttached?.Invoke(x.Component);
            }
            ApplyComponentChanges();
        }

        private readonly Stopwatch m_rrStopwatch = new Stopwatch();
        private int m_rrUpdateBeforeHead = 0, m_rrUpdateAfterHead = 0;
        public void UpdateBeforeSimulation()
        {
            if (!m_attached) return;
            m_rrStopwatch.Restart();
            ApplyComponentChanges();
            foreach (var x in m_orderedComponentList)
                x.Component.UpdateBeforeSimulation();

            var ticksSinceChanged = 0;
            while (m_rrStopwatch.Elapsed < TolerableLag && ticksSinceChanged < m_orderedComponentList.Count)
            {
                if (m_orderedComponentList[m_rrUpdateBeforeHead].Component.TickBeforeSimulationRoundRobin())
                    ticksSinceChanged = 0;
                else
                    ticksSinceChanged++;
                m_rrUpdateBeforeHead = (m_rrUpdateBeforeHead + 1) % m_orderedComponentList.Count;
            }
        }

        public void UpdateAfterSimulation()
        {
            if (!m_attached) return;
            m_rrStopwatch.Restart();
            ApplyComponentChanges();
            foreach (var x in m_orderedComponentList)
                x.Component.UpdateAfterSimulation();

            var ticksSinceChanged = 0;
            while (m_rrStopwatch.Elapsed < TolerableLag && ticksSinceChanged < m_orderedComponentList.Count)
            {
                if (m_orderedComponentList[m_rrUpdateAfterHead].Component.TickAfterSimulationRoundRobin())
                    ticksSinceChanged = 0;
                else
                    ticksSinceChanged++;
                m_rrUpdateAfterHead = (m_rrUpdateAfterHead + 1) % m_orderedComponentList.Count;
            }
        }

        public void Save()
        {
            if (!m_attached) return;
            ApplyComponentChanges();
            foreach (var x in m_orderedComponentList)
                x.Component.Save();
        }

        public void Detach()
        {
            ApplyComponentChanges();
            if (m_attached)
                for (var i = m_orderedComponentList.Count - 1; i >= 0; i--)
                {
                    m_orderedComponentList[i].Component.Detached();
                    ComponentDetached?.Invoke(m_orderedComponentList[i].Component);
                }
            m_attached = false;
            m_orderedComponentList.Clear();
        }

        public Ob_SessionManager SaveConfiguration()
        {
            var res = new Ob_SessionManager
            {
                TolerableLag = TolerableLag,
                SessionComponents = new List<Ob_ModSessionComponent>()
            };
            foreach (var k in m_componentDictionary.Values.SelectMany(x => x))
                if (k.Component.SaveToStorage)
                    res.SessionComponents.Add(k.Component.SaveConfiguration());
            return res;
        }

        public void AppendConfiguration(Ob_SessionManager config)
        {
            if (config.SessionComponents == null) return;
            TolerableLag = config.TolerableLag;
            foreach (var x in config.SessionComponents)
            {
                var desc = ModSessionComponentRegistry.Get(x);
                var module = desc.Activator();
                FallbackLogger.Log(MyLogSeverity.Debug, "Registering module {0} from configuration", module.GetType());
                Register(module, x);
            }
        }
    }

    [Serializable]
    [ProtoContract]
    public partial class Ob_SessionManager
    {
        // This is declared in MyModSessionComponentRegistryGen so it can be auto-generated.
        // public List<MyObjectBuilder_ModSessionComponent> SessionComponents = new List<MyObjectBuilder_ModSessionComponent>();

        [XmlIgnore]
        public TimeSpan TolerableLag = TimeSpan.FromSeconds(MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS * 0.5);

        [ProtoMember]
        public double TolerableLagSeconds
        {
            get { return TolerableLag.TotalSeconds; }
            set { TolerableLag = TimeSpan.FromSeconds(value); }
        }
    }
}
