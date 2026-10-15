using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace BeamCheck.Core.Report
{
    /// <summary>
    /// Minimal .xlsx writer (no Excel, no external library): sheets of text and numbers, bold header row, column widths,
    /// frozen header, auto filter and PNG pictures. Opens in Excel and LibreOffice.
    /// </summary>
    public sealed class XlsxBuilder
    {
        public sealed class Picture
        {
            public byte[] Png;
            public int Row;          // 0-based top-left cell
            public int Column;
            public int WidthPx;
            public int HeightPx;
        }

        public sealed class Sheet
        {
            public string Name;
            public List<object[]> Rows = new List<object[]>();
            public List<double> ColumnWidths = new List<double>();
            public List<Picture> Pictures = new List<Picture>();
            public bool FreezeHeader = true;
            public bool AutoFilter = true;

            /// <summary>Row heights in points by row index (rows with pictures).</summary>
            public Dictionary<int, double> RowHeights = new Dictionary<int, double>();

            /// <summary>Rows (0-based) that get the bold style, besides the header.</summary>
            public HashSet<int> BoldRows = new HashSet<int>();
        }

        public List<Sheet> Sheets { get; } = new List<Sheet>();

        public Sheet AddSheet(string name)
        {
            var s = new Sheet { Name = SafeSheetName(name, Sheets.Count) };
            Sheets.Add(s);
            return s;
        }

        private static string SafeSheetName(string name, int index)
        {
            var bad = new HashSet<char>("[]:*?/\\");
            var n = new string((name ?? "Sheet").Where(c => !bad.Contains(c)).ToArray());
            if (n.Length == 0)
                n = "Sheet" + (index + 1).ToString(CultureInfo.InvariantCulture);
            return n.Length > 31 ? n.Substring(0, 31) : n;
        }

        public void Save(string path)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
                Save(fs);
        }

        public void Save(Stream stream)
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                int imageCount = Sheets.Sum(s => s.Pictures.Count);
                var ct = new StringBuilder();
                ct.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
                ct.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>");
                if (imageCount > 0)
                    ct.Append("<Default Extension=\"png\" ContentType=\"image/png\"/>");
                ct.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
                ct.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
                for (int i = 0; i < Sheets.Count; i++)
                {
                    ct.AppendFormat("<Override PartName=\"/xl/worksheets/sheet{0}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>", i + 1);
                    if (Sheets[i].Pictures.Count > 0)
                        ct.AppendFormat("<Override PartName=\"/xl/drawings/drawing{0}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>", i + 1);
                }

                ct.Append("</Types>");
                Add(zip, "[Content_Types].xml", ct.ToString());
                Add(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");

                var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
                var wbRels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                for (int i = 0; i < Sheets.Count; i++)
                {
                    wb.AppendFormat("<sheet name=\"{0}\" sheetId=\"{1}\" r:id=\"rId{1}\"/>", Esc(Sheets[i].Name), i + 1);
                    wbRels.AppendFormat("<Relationship Id=\"rId{0}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{0}.xml\"/>", i + 1);
                }

                wb.Append("</sheets></workbook>");
                wbRels.AppendFormat("<Relationship Id=\"rId{0}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>", Sheets.Count + 1);
                Add(zip, "xl/workbook.xml", wb.ToString());
                Add(zip, "xl/_rels/workbook.xml.rels", wbRels.ToString());
                Add(zip, "xl/styles.xml", Styles);

                int image = 0;
                for (int i = 0; i < Sheets.Count; i++)
                {
                    var sh = Sheets[i];
                    Add(zip, "xl/worksheets/sheet" + (i + 1) + ".xml", SheetXml(sh));
                    if (sh.Pictures.Count == 0)
                        continue;
                    Add(zip, "xl/worksheets/_rels/sheet" + (i + 1) + ".xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing" + (i + 1) + ".xml\"/></Relationships>");
                    var dr = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
                    var drRels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                    int k = 0;
                    foreach (var p in sh.Pictures)
                    {
                        k++;
                        image++;
                        string media = "image" + image + ".png";
                        using (var es = zip.CreateEntry("xl/media/" + media).Open())
                            es.Write(p.Png, 0, p.Png.Length);
                        drRels.AppendFormat("<Relationship Id=\"rId{0}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/{1}\"/>", k, media);
                        long cx = (long)p.WidthPx * 9525, cy = (long)p.HeightPx * 9525;
                        dr.AppendFormat(CultureInfo.InvariantCulture,
                            "<xdr:oneCellAnchor><xdr:from><xdr:col>{0}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{1}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx=\"{2}\" cy=\"{3}\"/>" +
                            "<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"{4}\" name=\"Bild {4}\"/><xdr:cNvPicPr/></xdr:nvPicPr><xdr:blipFill><a:blip r:embed=\"rId{4}\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>" +
                            "<xdr:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{2}\" cy=\"{3}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic><xdr:clientData/></xdr:oneCellAnchor>",
                            p.Column, p.Row, cx, cy, k);
                    }

                    dr.Append("</xdr:wsDr>");
                    drRels.Append("</Relationships>");
                    Add(zip, "xl/drawings/drawing" + (i + 1) + ".xml", dr.ToString());
                    Add(zip, "xl/drawings/_rels/drawing" + (i + 1) + ".xml.rels", drRels.ToString());
                }
            }
        }

        private const string Styles =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"0.00\"/></numFmts>" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9E2F3\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"5\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +                                                     // 0 plain
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"><alignment wrapText=\"1\" vertical=\"center\"/></xf>" + // 1 header
            "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +                              // 2 0.00
            "<xf numFmtId=\"1\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +                                // 3 0
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +                                        // 4 bold
            "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

        private static string ColName(int c)
        {
            var sb = new StringBuilder();
            c++;
            while (c > 0)
            {
                c--;
                sb.Insert(0, (char)('A' + c % 26));
                c /= 26;
            }

            return sb.ToString();
        }

        private static string SheetXml(Sheet sh)
        {
            var inv = CultureInfo.InvariantCulture;
            int cols = sh.Rows.Count == 0 ? 1 : sh.Rows.Max(r => r.Length);
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
            if (sh.FreezeHeader && sh.Rows.Count > 1)
                sb.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            sb.Append("</sheetView></sheetViews>");
            if (sh.ColumnWidths.Count > 0)
            {
                sb.Append("<cols>");
                for (int i = 0; i < sh.ColumnWidths.Count; i++)
                    sb.AppendFormat(inv, "<col min=\"{0}\" max=\"{0}\" width=\"{1}\" customWidth=\"1\"/>", i + 1, sh.ColumnWidths[i]);
                sb.Append("</cols>");
            }

            sb.Append("<sheetData>");
            for (int r = 0; r < sh.Rows.Count; r++)
            {
                sb.AppendFormat("<row r=\"{0}\"", r + 1);
                if (sh.RowHeights.TryGetValue(r, out double h))
                    sb.AppendFormat(inv, " ht=\"{0}\" customHeight=\"1\"", h);
                sb.Append(">");
                var row = sh.Rows[r];
                for (int c = 0; c < row.Length; c++)
                {
                    object v = row[c];
                    if (v == null)
                        continue;
                    string refc = ColName(c) + (r + 1);
                    int style = r == 0 ? 1 : sh.BoldRows.Contains(r) ? 4 : 0;
                    if (v is string str)
                    {
                        sb.AppendFormat("<c r=\"{0}\" t=\"inlineStr\" s=\"{1}\"><is><t xml:space=\"preserve\">{2}</t></is></c>", refc, style, Esc(str));
                    }
                    else if (v is double d)
                    {
                        if (double.IsNaN(d) || double.IsInfinity(d))
                            continue;
                        sb.AppendFormat(inv, "<c r=\"{0}\" s=\"{1}\"><v>{2}</v></c>", refc, r == 0 ? 1 : 2, d.ToString("R", inv));
                    }
                    else if (v is int || v is long)
                    {
                        sb.AppendFormat(inv, "<c r=\"{0}\" s=\"{1}\"><v>{2}</v></c>", refc, r == 0 ? 1 : 3, Convert.ToInt64(v, inv));
                    }
                    else
                    {
                        sb.AppendFormat("<c r=\"{0}\" t=\"inlineStr\" s=\"{1}\"><is><t xml:space=\"preserve\">{2}</t></is></c>", refc, style, Esc(Convert.ToString(v, inv)));
                    }
                }

                sb.Append("</row>");
            }

            sb.Append("</sheetData>");
            if (sh.AutoFilter && sh.Rows.Count > 1)
                sb.AppendFormat("<autoFilter ref=\"A1:{0}{1}\"/>", ColName(cols - 1), sh.Rows.Count);
            if (sh.Pictures.Count > 0)
                sb.Append("<drawing r:id=\"rId1\"/>");
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        private static string Esc(string s) =>
            new string((s ?? "").Where(c => c >= ' ' || c == '\t' || c == '\n').ToArray()).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        private static void Add(ZipArchive zip, string name, string content)
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            using (var s = zip.CreateEntry(name).Open())
                s.Write(bytes, 0, bytes.Length);
        }
    }
}
