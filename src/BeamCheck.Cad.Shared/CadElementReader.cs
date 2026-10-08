using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Recognition;
using BeamCheck.Core.Settings;
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
    ///
    /// Geometry is always reduced to axis line segments in world mm:
    ///  - PERI CAD parts (custom entities such as PERI_AEC_DB_BAUTEIL) are exploded; the PERI display
    ///    block found inside ("PERI_&lt;article&gt;_PartDisplayName_3D") gives the article and the
    ///    transform, and the sibling "_Line" block gives the part's axis lines;
    ///  - ordinary blocks contribute their lines or, failing that, their bounding-box edges;
    ///  - anything else contributes its lines or bounding-box edges.
    /// The kind comes from the settings rules, otherwise from the shape of those segments.
    /// </summary>
    internal sealed class CadElementReader
    {
        private const int MaxNesting = 4;

        private readonly Transaction _tr;
        private readonly Database _db;
        private readonly BeamCheckSettings _settings;
        private readonly ElementClassifier _classifier;
        private readonly ShapeAnalyzer _shape;
        private readonly double _toMm;
        private readonly Regex _periBlock;
        private readonly Regex _ignoreDxf;
        private readonly Dictionary<ObjectId, Extents3d?> _blockExtents = new Dictionary<ObjectId, Extents3d?>();
        private readonly Dictionary<string, ObjectId> _blockByName = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);

        public CadElementReader(Transaction tr, Database db, BeamCheckSettings settings, ElementClassifier classifier, double unitToMm)
        {
            _tr = tr;
            _db = db;
            _settings = settings;
            _classifier = classifier;
            _shape = new ShapeAnalyzer(settings);
            _toMm = unitToMm;
            if (!string.IsNullOrEmpty(settings.PeriDisplayBlockPattern))
                _periBlock = new Regex(settings.PeriDisplayBlockPattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (!string.IsNullOrEmpty(settings.IgnoreDxfPattern))
                _ignoreDxf = new Regex(settings.IgnoreDxfPattern, RegexOptions.CultureInvariant);

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                _blockByName[btr.Name] = id;
            }
        }

        /// <summary>Element id → top-level entity (for highlighting).</summary>
        public Dictionary<string, ObjectId> TopLevelIds { get; } = new Dictionary<string, ObjectId>();

        /// <summary>
        /// Reads a top-level entity. With <paramref name="forcedKind"/> the entity itself is taken
        /// as that kind; otherwise it is classified and, if it is an unrecognised assembly block,
        /// searched for nested parts.
        /// </summary>
        public List<ScaffoldElement> Read(Entity ent, ElementKind? forcedKind = null)
        {
            var list = new List<ScaffoldElement>();
            if (forcedKind == null && IsIgnored(ent))
                return list;
            Read(ent, Matrix3d.Identity, ent.Handle.ToString(), ent.ObjectId, forcedKind, 0, list);
            return list;
        }

        public bool IsIgnored(Entity ent)
        {
            if (_ignoreDxf == null)
                return false;
            string dxf = DxfName(ent);
            return !string.IsNullOrEmpty(dxf) && _ignoreDxf.IsMatch(dxf);
        }

        private void Read(Entity ent, Matrix3d parent, string id, ObjectId topId, ElementKind? forcedKind, int depth, List<ScaffoldElement> output)
        {
            var sig = ReadSignature(_tr, ent);
            var segs = new List<Segment>();
            string periBlock = null;
            CollectSegments(ent, parent, segs, ref periBlock, 0);
            if (periBlock != null)
            {
                sig.Add("PERI:BLOCK", periBlock);
                if (string.IsNullOrEmpty(sig.BlockName))
                    sig.BlockName = periBlock;
            }

            var cls = _classifier.Classify(sig);
            var kind = forcedKind ?? cls.Kind;

            // An unrecognised ordinary block may be an assembly: look for parts inside first.
            if (kind == ElementKind.Unknown && periBlock == null && ent is BlockReference br && depth < MaxNesting)
            {
                int before = output.Count;
                var btr = (BlockTableRecord)_tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                var xf = parent * br.BlockTransform;
                foreach (ObjectId childId in btr)
                {
                    if (_tr.GetObject(childId, OpenMode.ForRead) is Entity child && !(child is AttributeDefinition) && !IsIgnored(child))
                        Read(child, xf, id + "/" + child.Handle, topId, null, depth + 1, output);
                }

                if (output.Count > before)
                    return;
            }

            if (kind == ElementKind.Unknown && _settings.UseShapeRecognition)
                kind = _shape.Guess(segs);
            if (kind == ElementKind.Unknown)
                return;

            var e = _shape.Build(segs, kind);
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

        // ---------------- geometry ----------------

        /// <summary>Collects the axis/edge segments of an entity in world mm.</summary>
        public void CollectSegments(Entity ent, Matrix3d xf, List<Segment> segs, ref string periBlock, int depth)
        {
            switch (ent)
            {
                case BlockReference br:
                {
                    var name = EffectiveName(_tr, br);
                    var bxf = xf * br.BlockTransform;
                    var m = _periBlock?.Match(name);
                    if (m != null && m.Success)
                    {
                        periBlock = periBlock ?? name;
                        string axisName = "PERI_" + m.Groups[1].Value + "_PartDisplayName_" + _settings.PeriAxisBlockSuffix;
                        if (_blockByName.TryGetValue(axisName, out var axisBtr) && AddBlockLines(axisBtr, bxf, segs) > 0)
                            return;
                    }

                    if (depth < MaxNesting)
                    {
                        int before = segs.Count;
                        var btr = (BlockTableRecord)_tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                        foreach (ObjectId id in btr)
                        {
                            if (_tr.GetObject(id, OpenMode.ForRead) is Entity child && !(child is AttributeDefinition))
                                CollectSegments(child, bxf, segs, ref periBlock, depth + 1);
                        }

                        if (segs.Count > before)
                            return;
                    }

                    var ext = BlockDefinitionExtents(br.BlockTableRecord);
                    if (ext.HasValue)
                        AddBoxEdges(ext.Value, bxf, segs);
                    return;
                }

                case Line line:
                    AddSegment(line.StartPoint, line.EndPoint, xf, segs);
                    return;

                case Polyline pl:
                    for (int i = 1; i < pl.NumberOfVertices; i++)
                        AddSegment(pl.GetPoint3dAt(i - 1), pl.GetPoint3dAt(i), xf, segs);
                    if (pl.Closed && pl.NumberOfVertices > 2)
                        AddSegment(pl.GetPoint3dAt(pl.NumberOfVertices - 1), pl.GetPoint3dAt(0), xf, segs);
                    return;

                case Polyline3d p3:
                {
                    Point3d? prev = null, first = null;
                    foreach (ObjectId vid in p3)
                    {
                        var v = (PolylineVertex3d)_tr.GetObject(vid, OpenMode.ForRead);
                        if (prev.HasValue)
                            AddSegment(prev.Value, v.Position, xf, segs);
                        first = first ?? v.Position;
                        prev = v.Position;
                    }

                    if (p3.Closed && prev.HasValue && first.HasValue)
                        AddSegment(prev.Value, first.Value, xf, segs);
                    return;
                }

                case Circle _:
                case Arc _:
                case Ellipse _:
                case DBText _:
                case MText _:
                    return; // details (rosettes, holes, labels) do not define the axis

                case Solid3d _:
                case Region _:
                    AddWorldExtents(ent, xf, segs);
                    return;
            }

            // Custom objects (PERI parts) and anything else: look at what they draw.
            if (depth < MaxNesting && TryExplode(ent, xf, segs, ref periBlock, depth))
                return;
            AddWorldExtents(ent, xf, segs);
        }

        private bool TryExplode(Entity ent, Matrix3d xf, List<Segment> segs, ref string periBlock, int depth)
        {
            var parts = new DBObjectCollection();
            try
            {
                ent.Explode(parts);
            }
            catch (System.Exception)
            {
                return false;
            }

            int before = segs.Count;
            foreach (DBObject o in parts)
            {
                if (o is Entity child)
                    CollectSegments(child, xf, segs, ref periBlock, depth + 1);
                o.Dispose();
            }

            return segs.Count > before;
        }

        private int AddBlockLines(ObjectId btrId, Matrix3d xf, List<Segment> segs)
        {
            int n = 0;
            var btr = (BlockTableRecord)_tr.GetObject(btrId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                if (_tr.GetObject(id, OpenMode.ForRead) is Line l)
                {
                    AddSegment(l.StartPoint, l.EndPoint, xf, segs);
                    n++;
                }
                else if (_tr.GetObject(id, OpenMode.ForRead) is Polyline pl)
                {
                    for (int i = 1; i < pl.NumberOfVertices; i++, n++)
                        AddSegment(pl.GetPoint3dAt(i - 1), pl.GetPoint3dAt(i), xf, segs);
                }
            }

            return n;
        }

        private void AddSegment(Point3d a, Point3d b, Matrix3d xf, List<Segment> segs)
        {
            var wa = a.TransformBy(xf);
            var wb = b.TransformBy(xf);
            if (wa.DistanceTo(wb) * _toMm < 1e-3)
                return;
            segs.Add(new Segment(ToVec(wa), ToVec(wb)));
        }

        private void AddWorldExtents(Entity ent, Matrix3d xf, List<Segment> segs)
        {
            try
            {
                AddBoxEdges(ent.GeometricExtents, xf, segs);
            }
            catch (System.Exception)
            {
                // No extents: contributes nothing.
            }
        }

        private void AddBoxEdges(Extents3d ext, Matrix3d xf, List<Segment> segs)
        {
            var min = ext.MinPoint;
            var max = ext.MaxPoint;
            var corners = new List<Vec3>(8);
            for (int i = 0; i < 8; i++)
            {
                var p = new Point3d((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
                corners.Add(ToVec(p.TransformBy(xf)));
            }

            foreach (var s in ShapeAnalyzer.BoxEdges(corners))
                if (s.Length > 1e-3)
                    segs.Add(s);
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

        // ---------------- signature ----------------

        public static string DxfName(Entity ent)
        {
            try
            {
                return ent.GetRXClass().DxfName;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        public static ElementSignature ReadSignature(Transaction tr, Entity ent)
        {
            var sig = new ElementSignature
            {
                Handle = ent.Handle.ToString(),
                EntityType = ent.GetType().Name,
                Layer = ent.Layer,
                DxfName = DxfName(ent),
            };

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

            ComProperties.AddTo(sig, ent);
            return sig;
        }

        public static string EffectiveName(Transaction tr, BlockReference br)
        {
            try
            {
                var btrId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                return ((BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead)).Name;
            }
            catch (System.Exception)
            {
                // Exploded (non-database-resident) references may not answer IsDynamicBlock.
                return ((BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead)).Name;
            }
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
    }
}
