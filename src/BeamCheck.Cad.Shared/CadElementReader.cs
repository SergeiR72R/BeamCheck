using System;
using System.Collections.Generic;
using System.Globalization;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Recognition;
#if BRICSCAD
using Teigha.DatabaseServices;
using Teigha.Geometry;
#else
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif

namespace BeamCheck.Cad
{
    /// <summary>
    /// Converts CAD entities into <see cref="ScaffoldElement"/>s.
    /// Unrecognised block references are searched recursively, so components
    /// nested in PERI assembly blocks are found as well.
    /// </summary>
    internal sealed class CadElementReader
    {
        private const int MaxNesting = 4;

        private readonly Transaction _tr;
        private readonly ElementClassifier _classifier;
        private readonly double _toMm;
        private readonly Dictionary<ObjectId, Extents3d?> _blockExtents = new Dictionary<ObjectId, Extents3d?>();

        public CadElementReader(Transaction tr, ElementClassifier classifier, double unitToMm)
        {
            _tr = tr;
            _classifier = classifier;
            _toMm = unitToMm;
        }

        /// <summary>Element id → top-level entity (for highlighting).</summary>
        public Dictionary<string, ObjectId> TopLevelIds { get; } = new Dictionary<string, ObjectId>();

        /// <summary>
        /// Reads a top-level entity. With <paramref name="forcedKind"/> the entity itself is taken
        /// as that kind; otherwise it is classified and, if unknown, searched for nested parts.
        /// </summary>
        public List<ScaffoldElement> Read(Entity ent, ElementKind? forcedKind = null)
        {
            var list = new List<ScaffoldElement>();
            Read(ent, Matrix3d.Identity, ent.Handle.ToString(), ent.ObjectId, forcedKind, 0, list);
            return list;
        }

        private void Read(Entity ent, Matrix3d parent, string id, ObjectId topId, ElementKind? forcedKind, int depth, List<ScaffoldElement> output)
        {
            var sig = ReadSignature(_tr, ent);
            var cls = _classifier.Classify(sig);
            var kind = forcedKind ?? cls.Kind;

            if (kind == ElementKind.Unknown)
            {
                if (ent is BlockReference br && depth < MaxNesting)
                {
                    var btr = (BlockTableRecord)_tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                    var xf = parent * br.BlockTransform;
                    foreach (ObjectId childId in btr)
                    {
                        if (_tr.GetObject(childId, OpenMode.ForRead) is Entity child && !(child is AttributeDefinition))
                            Read(child, xf, id + "/" + child.Handle, topId, null, depth + 1, output);
                    }
                }

                return;
            }

            var e = BuildGeometry(ent, parent, kind);
            if (e == null)
                return;

            e.Id = id;
            e.Kind = kind;
            e.Article = cls.Article;
            e.Description = cls.Description;
            if (cls.WeightKg.HasValue)
            {
                e.WeightKg = cls.WeightKg.Value;
            }
            else
            {
                e.WeightKg = _classifier.DefaultWeightKg(kind, kind == ElementKind.Standard ? e.ZMax - e.ZMin : e.Length, e.Width);
                e.WeightIsEstimated = true;
            }

            TopLevelIds[id] = topId;
            output.Add(e);
        }

        public static ElementSignature ReadSignature(Transaction tr, Entity ent)
        {
            var sig = new ElementSignature
            {
                Handle = ent.Handle.ToString(),
                EntityType = ent.GetType().Name,
                Layer = ent.Layer,
            };

            try
            {
                sig.DxfName = ent.GetRXClass().DxfName;
            }
            catch (System.Exception)
            {
            }

            if (ent is BlockReference br)
            {
                sig.BlockName = EffectiveName(tr, br);

                foreach (ObjectId attId in br.AttributeCollection)
                {
                    if (tr.GetObject(attId, OpenMode.ForRead) is AttributeReference ar)
                        sig.Add("ATTR:" + ar.Tag, ar.TextString);
                }

                var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                if (btr.HasAttributeDefinitions)
                {
                    foreach (ObjectId id in btr)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition ad && ad.Constant)
                            sig.Add("ATTR:" + ad.Tag, ad.TextString);
                    }
                }

                if (br.IsDynamicBlock)
                {
                    foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                        sig.Add("DYN:" + p.PropertyName, Convert.ToString(p.Value, CultureInfo.InvariantCulture));
                }
            }

            AddResultBuffer(sig, "XDATA", ent.XData);

            if (!ent.ExtensionDictionary.IsNull && tr.GetObject(ent.ExtensionDictionary, OpenMode.ForRead) is DBDictionary dict)
                AddDictionary(tr, sig, "XDICT", dict, 0);

            return sig;
        }

        public static string EffectiveName(Transaction tr, BlockReference br)
        {
            var btrId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            return ((BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead)).Name;
        }

        private static void AddDictionary(Transaction tr, ElementSignature sig, string prefix, DBDictionary dict, int depth)
        {
            foreach (DBDictionaryEntry entry in dict)
            {
                var obj = tr.GetObject(entry.Value, OpenMode.ForRead);
                if (obj is Xrecord xr)
                    AddResultBuffer(sig, prefix + ":" + entry.Key, xr.Data);
                else if (obj is DBDictionary sub && depth < 2)
                    AddDictionary(tr, sig, prefix + ":" + entry.Key, sub, depth + 1);
            }
        }

        private static void AddResultBuffer(ElementSignature sig, string prefix, ResultBuffer rb)
        {
            if (rb == null)
                return;
            using (rb)
            {
                string app = "";
                int i = 0;
                foreach (TypedValue tv in rb)
                {
                    if (tv.TypeCode == (short)DxfCode.ExtendedDataRegAppName)
                    {
                        app = Convert.ToString(tv.Value, CultureInfo.InvariantCulture);
                        i = 0;
                        continue;
                    }

                    sig.Add(prefix + ":" + app + "." + (i++).ToString(CultureInfo.InvariantCulture),
                        Convert.ToString(tv.Value, CultureInfo.InvariantCulture));
                }
            }
        }

        // ---------------- geometry ----------------

        private ScaffoldElement BuildGeometry(Entity ent, Matrix3d parent, ElementKind kind)
        {
            // Local frame: the block's own coordinate system when available, so that
            // rotated components still get their true length/width axes.
            Matrix3d frame = parent;
            Extents3d? local = null;
            if (ent is BlockReference br)
            {
                local = BlockDefinitionExtents(br.BlockTableRecord);
                if (local.HasValue)
                    frame = parent * br.BlockTransform;
            }

            if (!local.HasValue)
            {
                try
                {
                    local = ent.GeometricExtents;
                }
                catch (System.Exception)
                {
                    return null;
                }

                frame = parent;
            }

            var min = local.Value.MinPoint;
            var max = local.Value.MaxPoint;
            var c = new Point3d((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
            var size = new[] { max.X - min.X, max.Y - min.Y, max.Z - min.Z };
            var axes = new[] { Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis };

            // World AABB from the 8 transformed corners.
            double wx0 = double.MaxValue, wy0 = double.MaxValue, wz0 = double.MaxValue;
            double wx1 = double.MinValue, wy1 = double.MinValue, wz1 = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = new Point3d((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z).TransformBy(frame);
                wx0 = Math.Min(wx0, p.X); wy0 = Math.Min(wy0, p.Y); wz0 = Math.Min(wz0, p.Z);
                wx1 = Math.Max(wx1, p.X); wy1 = Math.Max(wy1, p.Y); wz1 = Math.Max(wz1, p.Z);
            }

            var e = new ScaffoldElement { ZMin = wz0 * _toMm, ZMax = wz1 * _toMm };

            if (kind == ElementKind.Standard)
            {
                double cx = (wx0 + wx1) / 2 * _toMm, cy = (wy0 + wy1) / 2 * _toMm;
                e.Start = new Vec3(cx, cy, e.ZMin);
                e.End = new Vec3(cx, cy, e.ZMax);
                e.Width = Math.Max(wx1 - wx0, wy1 - wy0) * _toMm;
                return e;
            }

            // Sort local axes by extent: [0] = length axis.
            var order = new[] { 0, 1, 2 };
            Array.Sort(order, (a, b) => size[b].CompareTo(size[a]));
            int lengthAxis = order[0];

            // Width: of the two remaining axes, the more horizontal one in world space.
            int widthAxis = order[1];
            var w1 = axes[order[1]].TransformBy(frame);
            var w2 = axes[order[2]].TransformBy(frame);
            if (kind == ElementKind.Beam && Math.Abs(w2.GetNormal().Z) < Math.Abs(w1.GetNormal().Z))
                widthAxis = order[2];

            var half = axes[lengthAxis] * (size[lengthAxis] / 2);
            e.Start = ToVec((c - half).TransformBy(frame));
            e.End = ToVec((c + half).TransformBy(frame));

            var halfW = axes[widthAxis] * (size[widthAxis] / 2);
            e.Width = (c + halfW).TransformBy(frame).DistanceTo((c - halfW).TransformBy(frame)) * _toMm;
            return e;
        }

        private Vec3 ToVec(Point3d p) => new Vec3(p.X * _toMm, p.Y * _toMm, p.Z * _toMm);

        /// <summary>Extents of a block definition's content in block coordinates (cached).</summary>
        private Extents3d? BlockDefinitionExtents(ObjectId btrId)
        {
            if (_blockExtents.TryGetValue(btrId, out var cached))
                return cached;

            Extents3d? ext = null;
            var btr = (BlockTableRecord)_tr.GetObject(btrId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                if (!(_tr.GetObject(id, OpenMode.ForRead) is Entity child) || child is AttributeDefinition)
                    continue;
                try
                {
                    var ce = child.GeometricExtents;
                    if (ext.HasValue)
                    {
                        var acc = ext.Value;
                        acc.AddExtents(ce);
                        ext = acc;
                    }
                    else
                    {
                        ext = ce;
                    }
                }
                catch (System.Exception)
                {
                    // Entities without extents (e.g. empty text) are ignored.
                }
            }

            _blockExtents[btrId] = ext;
            return ext;
        }
    }
}
