#if !BRICSCAD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using BeamCheck.Core.Model;
using BeamCheck.Core.Recognition;
using BeamCheck.Core.Settings;
using BeamCheck.Core.Spindles;

namespace BeamCheck.Cad
{
    /// <summary>
    /// Reads the drawing for SPINDELLOAD with BeamCheck's own reader, so PERI CAD objects (PERIAecDbBauteil …) are recognised
    /// exactly as BEAMLOAD / BEAMBATCH recognise them. Parts matching SpindlePattern become spindles, parts matching
    /// SupportEndPartPattern (nuts, base plates, pins) spindle components. Objects that are never scaffold parts but can take
    /// forever to read (XLINE / RAY, xrefs, huge blocks and objects, skipped layers) are left out before reading.
    /// </summary>
    internal sealed class SpindleScanner
    {
        private readonly Database _db;
        private readonly SpindleSettings _s;
        private readonly Transaction _tr;
        private readonly double _unit;
        private readonly CadElementReader _reader;
        private readonly ElementClassifier _classifier;

        public SpindleScanner(Database db, SpindleSettings s, BeamCheckSettings bs, Transaction tr)
        {
            _db = db; _s = s; _tr = tr;
            _unit = CadUnits.ToMm(db, bs);
            _classifier = new ElementClassifier(bs);
            _reader = new CadElementReader(tr, db, bs, _classifier, _unit)
            {
                SpindleRx = s.SpindleRx,
                ComponentRx = s.SupportEndRx,
                CollectUnknowns = true,
            };
        }

        /// <summary>Element id → top-level drawing object (to highlight, isolate, zoom).</summary>
        public Dictionary<string, ObjectId> Ids { get; } = new Dictionary<string, ObjectId>();

        // -------------------------------------------------------------- scanning

        public ScanResult ScanModelSpace(Func<int, int, string, bool> progress = null)
        {
            var ms = (BlockTableRecord)_tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(_db), OpenMode.ForRead);
            var ids = new List<ObjectId>();
            foreach (ObjectId id in ms) ids.Add(id);
            return Scan(ids, progress);
        }

        public ScanResult ScanEntities(IEnumerable<ObjectId> ids, Func<int, int, string, bool> progress = null)
        {
            return Scan(ids.ToList(), progress);
        }

        private ScanResult Scan(List<ObjectId> ids, Func<int, int, string, bool> progress)
        {
            var res = new ScanResult { UnitToMm = _unit };
            var elements = new List<ScaffoldElement>();
            var clock = new Stopwatch();
            Regex skipLayers = string.IsNullOrEmpty(_s.SkipLayersPattern) ? null : new Regex(_s.SkipLayersPattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            int n = 0;
            foreach (var id in ids)
            {
                n++;
                string what = id.ObjectClass.DxfName + " " + id.Handle;
                if (progress != null && !progress(n, ids.Count, what)) { res.Cancelled = true; break; }
                try
                {
                    var ent = _tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || IsAnnotation(ent) || !IsVisible(ent)) continue;
                    string why = SkipReason(ent, skipLayers);
                    if (why != null) { res.Ignored.Add(why); continue; }
                    clock.Restart();
                    var got = _reader.Read(ent);
                    clock.Stop();
                    if (clock.ElapsedMilliseconds > 2000) res.SlowObjects.Add(what + " (" + ent.Layer + "): " + (clock.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s");
                    foreach (var el in got)
                    {
                        if (Sane(el)) elements.Add(el);
                        else res.Ignored.Add("part with impossible coordinates / size");
                    }
                }
                catch (Exception ex)
                {
                    res.Skipped++;
                    if (res.FirstError == null) res.FirstError = ex.Message;
                }
            }

            foreach (var el in elements)
            {
                res.Add(el, _reader.SpindleIds.Contains(el.Id), _reader.ComponentIds.Contains(el.Id));
                ObjectId top;
                if (_reader.TopLevelIds.TryGetValue(el.Id, out top)) Ids[el.Id] = top;
            }

            foreach (var u in _reader.Unknowns)
            {
                if (res.PartById.ContainsKey(u.Id)) continue;
                var up = new PartInfo
                {
                    Handle = u.Id, TopHandle = SpindleEngine.TopHandle(u.Id), Kind = PartKind.Other, Name = u.Text ?? "",
                    MinX = u.Extents.MinPoint.X * _unit, MaxX = u.Extents.MaxPoint.X * _unit,
                    MinY = u.Extents.MinPoint.Y * _unit, MaxY = u.Extents.MaxPoint.Y * _unit,
                    MinZ = u.Extents.MinPoint.Z * _unit, MaxZ = u.Extents.MaxPoint.Z * _unit,
                };
                up.Ax = up.CenterX; up.Ay = up.CenterY; up.Az = up.MinZ; up.Bx = up.CenterX; up.By = up.CenterY; up.Bz = up.MaxZ;
                res.Unrecognized.Add(up);
                res.PartById[u.Id] = up;
                Ids[u.Id] = u.TopId;
            }

            return res;
        }

        // -------------------------------------------------------------- safety filters

        private readonly Dictionary<ObjectId, int> _blockCount = new Dictionary<ObjectId, int>();

        /// <summary>Objects that are never scaffold parts but can take forever (or be infinite): skipped before reading.</summary>
        private string SkipReason(Entity ent, Regex skipLayers)
        {
            if (ent is Ray || ent is Xline) return "construction line (XLINE / RAY)";
            if (skipLayers != null && skipLayers.IsMatch(ent.Layer ?? "")) return "layer " + ent.Layer + " (SkipLayersPattern)";
            var br = ent as BlockReference;
            if (br != null)
            {
                try
                {
                    var btr = (BlockTableRecord)_tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                    if (btr.IsFromExternalReference || btr.IsDependent) return "xref " + btr.Name;
                    int cnt = CountEntities(br.BlockTableRecord, 0);
                    if (cnt > _s.MaxBlockEntities) return "large block " + btr.Name + " (" + cnt + "+ objects, MaxBlockEntities)";
                }
                catch (Exception) { }
            }
            try
            {
                var ext = ent.GeometricExtents;
                double dx = (ext.MaxPoint.X - ext.MinPoint.X) * _unit, dy = (ext.MaxPoint.Y - ext.MinPoint.Y) * _unit, dz = (ext.MaxPoint.Z - ext.MinPoint.Z) * _unit;
                double size = Math.Max(dx, Math.Max(dy, dz));
                if (double.IsNaN(size) || double.IsInfinity(size)) return "object without valid size";
                if (size > _s.MaxObjectSize) return "object larger than " + (_s.MaxObjectSize / 1000).ToString("0") + " m (MaxObjectSize)";
            }
            catch (Exception) { }
            return null;
        }

        private int CountEntities(ObjectId btrId, int depth)
        {
            int c;
            if (_blockCount.TryGetValue(btrId, out c)) return c;
            c = 0;
            _blockCount[btrId] = int.MaxValue / 4;      // guard against recursive definitions
            try
            {
                var btr = (BlockTableRecord)_tr.GetObject(btrId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    c++;
                    if (depth < 6 && id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(BlockReference))))
                    {
                        var child = _tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (child != null) c += CountEntities(child.BlockTableRecord, depth + 1);
                    }
                    if (c > _s.MaxBlockEntities) break;
                }
            }
            catch (Exception) { }
            _blockCount[btrId] = c;
            return c;
        }

        private bool Sane(ScaffoldElement el)
        {
            double[] v = { el.Start.X, el.Start.Y, el.Start.Z, el.End.X, el.End.Y, el.End.Z, el.ZMin, el.ZMax, el.Width };
            foreach (var d in v) if (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > 1e9) return false;
            double len = Math.Max(Math.Abs(el.End.X - el.Start.X), Math.Max(Math.Abs(el.End.Y - el.Start.Y), Math.Abs(el.ZMax - el.ZMin)));
            return len <= _s.MaxObjectSize && el.Width <= _s.MaxObjectSize;
        }

        private bool IsVisible(Entity e)
        {
            if (!e.Visible) return false;
            try
            {
                var lt = (LayerTableRecord)_tr.GetObject(e.LayerId, OpenMode.ForRead);
                if (lt.IsFrozen || lt.IsOff) return false;
            }
            catch (Exception) { }
            return true;
        }

        private static bool IsAnnotation(Entity ent)
        {
            return ent is Dimension || ent is DBText || ent is MText || ent is Table || ent is Hatch || ent is Leader || ent is MLeader;
        }

        // -------------------------------------------------------------- SPINDELDUMP

        public List<string> Dump(ObjectId id)
        {
            var lines = new List<string>();
            var ent = _tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (ent == null) { lines.Add("Not an entity."); return lines; }

            var sig = CadElementReader.ReadSignature(_tr, ent);
            lines.Add("Object type    : " + ent.GetType().Name + "   DXF: " + (CadElementReader.DxfName(ent) ?? "-") + "   handle " + ent.Handle);
            lines.Add("Layer          : " + ent.Layer);
            if (!string.IsNullOrEmpty(sig.BlockName)) lines.Add("Block name     : " + sig.BlockName);
            int shown = 0;
            foreach (var kv in sig.Properties)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                if (++shown > 40) { lines.Add("  ... more properties not shown"); break; }
                lines.Add("  " + kv.Key + " = " + (kv.Value.Length > 80 ? kv.Value.Substring(0, 79) + "." : kv.Value));
            }

            var cls = _classifier.Classify(sig);
            lines.Add("-> article     : " + (string.IsNullOrEmpty(cls.Article) ? "(none)" : cls.Article));
            lines.Add("-> name        : " + (string.IsNullOrEmpty(cls.Description) ? "(none)" : cls.Description));
            lines.Add("-> BeamCheck   : " + cls.Kind + (cls.WeightKg.HasValue ? "   weight " + cls.WeightKg.Value.ToString("0.##") + " kg" : "   (no weight in table)"));

            var els = _reader.Read(ent);
            if (els.Count == 0) lines.Add("-> read result : nothing (part is NOT used in the calculation)");
            foreach (var el in els.Take(10))
            {
                string role = _reader.SpindleIds.Contains(el.Id) ? "SPINDLE"
                            : _reader.ComponentIds.Contains(el.Id) ? "spindle component (nut / base plate / pin)"
                            : el.Kind.ToString();
                lines.Add("-> used as     : " + role + "   " + el.WeightKg.ToString("0.##") + " kg" + (el.WeightIsEstimated ? " (estimated)" : "") +
                          "   Z " + el.ZMin.ToString("0") + ".." + el.ZMax.ToString("0") + " mm");
            }
            lines.Add("SpindlePattern matches name : " + (_s.SpindleRx.IsMatch((cls.Article ?? "") + " " + (cls.Description ?? "") + " " + (sig.BlockName ?? "")) ? "YES" : "no"));
            return lines;
        }
    }
}
#endif
