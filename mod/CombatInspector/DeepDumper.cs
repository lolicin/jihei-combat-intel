using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Entities;

namespace CombatInspector
{
    /// <summary>
    /// Reflective "give me literally everything" dumper.
    ///
    /// The curated <see cref="CombatScanner"/> covers the fields that matter for a combat HUD.
    /// This class instead walks <c>EntityManager.GetComponentTypes(entity)</c> and reads back every
    /// component - IComponentData, IBufferElementData, ISharedComponentData and zero-sized tags -
    /// through reflection, then flattens each struct's public fields into plain JSON-safe values.
    ///
    /// That means it keeps working when the game adds new components in a patch: no code change
    /// needed to see them.
    /// </summary>
    public static class DeepDumper
    {
        public const int MaxBufferElements = 128;
        public const int DefaultMaxDepth = 4;

        private static readonly MethodInfo MiGetComponentData = FindMethod(
            "GetComponentData", typeof(Entity));
        private static readonly MethodInfo MiGetBuffer = FindMethod(
            "GetBuffer", typeof(Entity), typeof(bool));
        private static readonly MethodInfo MiGetShared = FindMethod(
            "GetSharedComponentData", typeof(Entity));

        private static MethodInfo FindMethod(string name, params Type[] args)
        {
            try
            {
                return typeof(EntityManager).GetMethod(
                    name, BindingFlags.Public | BindingFlags.Instance, null, args, null);
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- entry points

        /// <summary>Deep dump of the local player plus the nearest enemies, ready to attach to a snapshot.</summary>
        public static Dictionary<string, object> CaptureDeep(int maxEnemies, int maxDepth, out string error)
        {
            error = null;
            var root = new Dictionary<string, object>();

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                error = "ECS world not ready";
                return root;
            }
            var em = world.EntityManager;

            Entity player = Entity.Null;
            try
            {
                using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerTag>()))
                {
                    if (!q.IsEmptyIgnoreFilter)
                    {
                        using (var arr = q.ToEntityArray(Unity.Collections.Allocator.Temp))
                        {
                            // In co-op every player mech carries PlayerTag; dump all of them.
                            var players = new List<object>();
                            for (int i = 0; i < arr.Length; i++)
                            {
                                players.Add(DumpEntity(em, arr[i], maxDepth));
                                if (i == 0) player = arr[i];
                            }
                            root["players"] = players;
                        }
                    }
                }
            }
            catch (Exception ex) { root["players_error"] = Describe(ex); }

            try
            {
                using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<EnemyTag>()))
                {
                    if (!q.IsEmptyIgnoreFilter)
                    {
                        using (var arr = q.ToEntityArray(Unity.Collections.Allocator.Temp))
                        {
                            var list = new List<object>();
                            int take = Math.Min(arr.Length, Math.Max(0, maxEnemies));
                            for (int i = 0; i < take; i++)
                                list.Add(DumpEntity(em, arr[i], maxDepth));
                            root["enemies"] = list;
                            root["enemiesTotal"] = arr.Length;
                            root["enemiesDumped"] = take;
                        }
                    }
                }
            }
            catch (Exception ex) { root["enemies_error"] = Describe(ex); }

            try
            {
                using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<BossTag>()))
                {
                    if (!q.IsEmptyIgnoreFilter)
                    {
                        using (var arr = q.ToEntityArray(Unity.Collections.Allocator.Temp))
                        {
                            var list = new List<object>();
                            for (int i = 0; i < arr.Length; i++)
                                list.Add(DumpEntity(em, arr[i], maxDepth));
                            root["bosses"] = list;
                        }
                    }
                }
            }
            catch (Exception ex) { root["bosses_error"] = Describe(ex); }

            return root;
        }

        /// <summary>Full component dump for one entity, keyed by component type name.</summary>
        public static Dictionary<string, object> DumpEntity(EntityManager em, Entity e, int maxDepth)
        {
            var result = new Dictionary<string, object>
            {
                ["__entity"] = "E" + e.Index + ":" + e.Version
            };

            if (!em.Exists(e))
            {
                result["__error"] = "entity does not exist";
                return result;
            }

            NativeArray<ComponentType> types;
            try { types = em.GetComponentTypes(e, Allocator.Temp); }
            catch (Exception ex) { result["__error"] = Describe(ex); return result; }

            try
            {
                for (int i = 0; i < types.Length; i++)
                {
                    ComponentType ct = types[i];
                    string key = SafeKey(ct.ToString());

                    Type t = null;
                    try { t = ct.GetManagedType(); } catch { }
                    if (t == null)
                    {
                        result[key] = "<unresolved component type (native-only)>";
                        continue;
                    }

                    try
                    {
                        if (ct.IsChunkComponent)
                        {
                            result[key] = "<chunk component: read with GetChunkComponentData<" + t.Name + ">>";
                        }
                        else if (ct.IsBuffer)
                        {
                            if (MiGetBuffer == null) { result[key] = "<GetBuffer not found>"; continue; }
                            object buf = MiGetBuffer.MakeGenericMethod(t).Invoke(em, new object[] { e, true });
                            result[key] = DumpEnumerable(buf, maxDepth);
                        }
                        else if (ct.IsSharedComponent)
                        {
                            if (MiGetShared == null) { result[key] = "<GetSharedComponentData not found>"; continue; }
                            object v = MiGetShared.MakeGenericMethod(t).Invoke(em, new object[] { e });
                            result[key] = SafeValue(v, maxDepth);
                        }
                        else if (ct.IsZeroSized)
                        {
                            result[key] = DescribeTag(em, e, ct);
                        }
                        else
                        {
                            if (MiGetComponentData == null) { result[key] = "<GetComponentData not found>"; continue; }
                            // EntityManager.GetComponentData<T> is constrained to `unmanaged`, so a
                            // managed IComponentData cannot be read this way. Say so instead of
                            // surfacing a confusing generic-constraint exception.
                            if (!LooksUnmanaged(t, new HashSet<Type>(), 6))
                            {
                                result[key] = "<managed component: not readable via GetComponentData<T> " +
                                              "(the unmanaged constraint excludes it)>";
                                continue;
                            }
                            object v = MiGetComponentData.MakeGenericMethod(t).Invoke(em, new object[] { e });
                            result[key] = SafeValue(v, maxDepth);
                        }
                    }
                    catch (Exception ex)
                    {
                        result[key] = "<read failed: " + Describe(ex) + ">";
                    }
                }
            }
            finally
            {
                try { types.Dispose(); } catch { }
            }

            return result;
        }

        // ---------------------------------------------------------------- internals

        private static string DescribeTag(EntityManager em, Entity e, ComponentType ct)
        {
            try
            {
                if (ct.IsEnableable)
                {
                    bool on = em.IsComponentEnabled(e, ct);
                    return "tag(enabled=" + (on ? "true" : "false") + ")";
                }
            }
            catch { }
            return "tag";
        }

        /// <summary>
        /// Best-effort runtime approximation of C#'s `unmanaged` constraint, which
        /// <c>EntityManager.GetComponentData&lt;T&gt;</c> requires.
        /// </summary>
        private static bool LooksUnmanaged(Type t, HashSet<Type> seen, int depth)
        {
            if (t == null) return false;
            if (t.IsPrimitive || t.IsEnum || t == typeof(decimal)) return true;
            if (!t.IsValueType) return false;
            if (t.IsPointer || t == typeof(IntPtr) || t == typeof(UIntPtr)) return true;
            if (depth <= 0) return true;
            if (!seen.Add(t)) return true;

            FieldInfo[] fs;
            try { fs = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
            catch { return false; }

            for (int i = 0; i < fs.Length; i++)
                if (!LooksUnmanaged(fs[i].FieldType, seen, depth - 1)) return false;
            return true;
        }

        private static object DumpEnumerable(object buf, int depth)
        {
            if (buf == null) return null;
            Type bt = buf.GetType();

            // IMPORTANT: in this Entities build DynamicBuffer<T> explicitly implements the
            // NON-generic IEnumerable.GetEnumerator() as `throw new NotImplementedException()`
            // (only the generic IEnumerable<T> one works). Enumerating it through IEnumerable
            // therefore blows up. Length + the int indexer works for DynamicBuffer<T>, for arrays
            // and for NativeList-like types, so prefer that path.
            PropertyInfo lenProp = null;
            PropertyInfo itemProp = null;
            try
            {
                lenProp = bt.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
                itemProp = bt.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance,
                    null, null, new[] { typeof(int) }, null);
            }
            catch { }

            if (lenProp != null && itemProp != null && lenProp.PropertyType == typeof(int))
            {
                int n;
                try { n = (int)lenProp.GetValue(buf, null); }
                catch (Exception ex) { return "<length read failed: " + Describe(ex) + ">"; }

                var list = new List<object>();
                int take = n < MaxBufferElements ? n : MaxBufferElements;
                for (int i = 0; i < take; i++)
                {
                    try { list.Add(SafeValue(itemProp.GetValue(buf, new object[] { i }), depth)); }
                    catch (Exception ex) { list.Add("<item read failed: " + Describe(ex) + ">"); }
                }
                if (n > take) list.Add("... truncated, " + (n - take) + " of " + n + " omitted");
                return list;
            }

            var en = buf as IEnumerable;
            if (en == null) return "<not enumerable>";

            var fallback = new List<object>();
            int k = 0;
            foreach (var item in en)
            {
                if (k++ >= MaxBufferElements) { fallback.Add("... truncated at " + MaxBufferElements); break; }
                fallback.Add(SafeValue(item, depth));
            }
            return fallback;
        }

        /// <summary>
        /// Converts an arbitrary value into something Newtonsoft can serialise without ever
        /// touching a raw pointer, a native container or a cyclic object graph.
        /// </summary>
        public static object SafeValue(object v, int depth)
        {
            if (v == null) return null;

            Type t = v.GetType();
            if (t.IsPrimitive || v is string || v is decimal) return v;
            if (t.IsEnum) return v.ToString();
            if (v is Entity) { var ent = (Entity)v; return "E" + ent.Index + ":" + ent.Version; }
            if (t == typeof(IntPtr) || t == typeof(UIntPtr) || t.IsPointer) return "<ptr>";

            string tn = t.Name;
            if (tn.StartsWith("FixedString"))
            {
                try { return v.ToString(); } catch { return "<fixed string>"; }
            }
            if (tn.StartsWith("BlobAssetReference") || tn.StartsWith("BlobArray") || tn.StartsWith("BlobPtr") || tn.StartsWith("BlobString"))
                return "<" + tn + ">";
            if (tn.StartsWith("NativeArray") || tn.StartsWith("NativeList") || tn.StartsWith("NativeSlice") ||
                tn.StartsWith("NativeHashMap") || tn.StartsWith("NativeParallel") || tn.StartsWith("NativeQueue") ||
                tn.StartsWith("NativeReference") || tn.StartsWith("DynamicBuffer"))
                return "<" + tn + " (native container, not dumped)>";
            if (t.Namespace != null && t.Namespace.StartsWith("Unity.Collections"))
                return "<" + tn + " (Unity.Collections)>";
            if (tn == "Random" && t.Namespace == "Unity.Mathematics")
            {
                try { return "Unity.Mathematics.Random(state=" + t.GetField("state").GetValue(v) + ")"; }
                catch { return "<random>"; }
            }

            if (v is UnityEngine.Object)
            {
                var uo = (UnityEngine.Object)v;
                return uo != null ? "UnityEngine.Object:" + uo.name : "<null UnityEngine.Object>";
            }

            if (v is IEnumerable) return DumpEnumerable(v, depth);

            if (depth <= 0) return "<" + tn + " (depth limit)>";

            FieldInfo[] fields;
            try { fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance); }
            catch { return "<" + tn + ">"; }

            if (fields.Length == 0)
            {
                // Fall back to public instance properties for wrapper types.
                PropertyInfo[] props;
                try { props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance); }
                catch { props = new PropertyInfo[0]; }
                if (props.Length == 0) return "<" + tn + " (no public fields)>";

                var pd = new Dictionary<string, object>();
                foreach (var p in props)
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                    try { pd[p.Name] = SafeValue(p.GetValue(v, null), depth - 1); }
                    catch { pd[p.Name] = "<err>"; }
                }
                return pd;
            }

            var d = new Dictionary<string, object>();
            foreach (var f in fields)
            {
                try { d[f.Name] = SafeValue(f.GetValue(v), depth - 1); }
                catch { d[f.Name] = "<err>"; }
            }
            return d;
        }

        private static string SafeKey(string k)
        {
            if (string.IsNullOrEmpty(k)) return "<unnamed>";
            return k;
        }

        private static string Describe(Exception ex)
        {
            var real = ex;
            var tie = ex as TargetInvocationException;
            if (tie != null && tie.InnerException != null) real = tie.InnerException;
            return real.GetType().Name + ": " + real.Message;
        }
    }
}
