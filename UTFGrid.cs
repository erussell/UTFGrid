using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ESRI.ArcGIS;
using ESRI.ArcGIS.Carto;
using ESRI.ArcGIS.esriSystem;
using ESRI.ArcGIS.Geodatabase;
using ESRI.ArcGIS.Geometry;
using Newtonsoft.Json;

namespace NatGeo
{
    class UTFGrid
    {
        [STAThread()]
        static void Main (string[] args) {
            if ((args.Length != 2) && (args.Length != 3)) {
                Console.WriteLine("Usage: UTFGrid.exe <mxd> <destination> (<scales>)");
                Console.WriteLine("   mxd: Path of mxd to be cooked");
                Console.WriteLine("   destination: Folder where utfgrid files are to be stored");
                Console.WriteLine("   scales (optional): List of scale levels between 0 and 19 inclusive,");
                Console.WriteLine("                      separated by commas");
                return;
            }
            RuntimeManager.BindLicense(ProductCode.EngineOrDesktop);
            string mapPath = args[0];
            string destination = args[1];
            int[] levels = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };
            if (args.Length > 2) {
                levels = args[2].Split(new char[] { ',' }).Select(s => Convert.ToInt32(s)).ToArray();
            }

            IMapDocument mapDocument = new MapDocumentClass();
            mapDocument.Open(mapPath, null);
            IMap map = mapDocument.ActiveView as IMap;
            if (map == null) {
                map = mapDocument.get_Map(0);
            }
            mapDocument.Close();
            if (map == null) {
                Console.WriteLine("Unable to open map at " + mapPath);
                return;
            }
            IActiveView activeView = map as IActiveView;
            // get the extent from the active view
            IEnvelope fullExtent = activeView.FullExtent;

            foreach (int level in levels) {
                foreach (TileDescription tile in DescribeTiles(level, fullExtent)) {
                    string folder = String.Format("{0}\\{1}\\{2}", destination, level, tile.Col);
                    if (!Directory.Exists(folder)) {
                        Directory.CreateDirectory(folder);
                    }
                    string file = String.Format("{0}\\{1}.grid.json", folder, tile.Row);
                    if (!File.Exists(file)) {
                        using (StreamWriter fOut = File.CreateText(file)) {
                            Dictionary<string, object> data = CollectData(map, tile.Extent, 128);
                            if (data.Count > 0) {
                                fOut.Write(JsonConvert.SerializeObject(data, Formatting.Indented));
                            }
                        }
                    }
                }
            }
        }

        private static Dictionary<string, object> CollectData (IMap map, IEnvelope extent, int tileSize) {
            double cellWidth = extent.Width / (tileSize - 1);
            double cellHeight = extent.Height / (tileSize - 1);
            Dictionary<ValueList, List<Cell>> dataCells = new Dictionary<ValueList, List<Cell>>();
            for (int y = 0; y < tileSize; y += 1) {
                for (int x = 0; x < tileSize; x += 1) {
                    EnvelopeClass pixelExtent = new EnvelopeClass();
                    pixelExtent.XMin = extent.XMin + x * cellWidth;
                    pixelExtent.XMax = pixelExtent.XMin + cellWidth;
                    pixelExtent.YMax = extent.YMax - y * cellHeight;
                    pixelExtent.YMin = pixelExtent.YMax - cellHeight;
                    ValueList cellData = GetPixelData(map, pixelExtent);
                    if (cellData.Count > 0) {
                        if (dataCells.ContainsKey(cellData)) {
                            dataCells[cellData].Add(new Cell(y, x));
                        } else {
                            List<Cell> cells = new List<Cell>();
                            cells.Add(new Cell(y, x));
                            dataCells[cellData] = cells;
                        }
                    }
                }
            }
            StringBuilder[] grid = new StringBuilder[tileSize];
            for (int i = 0; i < tileSize; i += 1)
                grid[i] = new StringBuilder(new String(' ', tileSize));
            List<string> keys = new List<string>();
            keys.Add("");
            Dictionary<string, object> data = new Dictionary<string, object>();
            int keyIndex = 0;
            foreach (KeyValuePair<ValueList, List<Cell>> d in dataCells) {
                if (d.Key.Count > 0) {
                    char code = EncodeChar(keys.Count);
                    string key = keyIndex.ToString();
                    foreach (Cell cell in d.Value) {
                        grid[cell.Row][cell.Col] = code;
                    }
                    keys.Add(key);
                    data.Add(key, d.Key);
                    keyIndex += 1;
                }
            }
            Dictionary<string, object> result = new Dictionary<string, object>();
            result.Add("grid", grid.Select(sb => sb.ToString()).ToArray());
            result.Add("keys", keys.ToArray());
            result.Add("data", data);
            return result;
        }
            
        private static ValueList GetPixelData (IMap map, IEnvelope extent) {
            ValueList result = new ValueList();
            for (int i = 0; i < map.LayerCount; i += 1) {
                ILayer layer = map.get_Layer(i);
                if (!layer.Visible) {
                    continue;
                }
                IIdentify id = layer as IIdentify;
                if (id == null) {
                    continue;
                }
                IArray data = id.Identify(extent);
                if (data == null) {
                    continue;
                }
                for (int j = 0; j < data.Count; j += 1) {
                    object foundObj = data.get_Element(j);
                    IRasterIdentifyObj2 raster = foundObj as IRasterIdentifyObj2;
                    if (raster != null) {
                        int propertyIndex = 0;
                        string property;
                        string value;
                        while (true) {
                            try {
                                raster.GetPropAndValues(propertyIndex, out property, out value);
                                if (!"NoData".Equals(value)) {
                                    result.Add(property, value);
                                }
                                propertyIndex += 1;
                            } catch {
                                break;
                            }
                        }
                        continue;
                    }  
                    IRowIdentifyObject row = foundObj as IRowIdentifyObject;
                    if (row != null) {
                        IFields fields = row.Row.Fields;
                        for (int k = 0; k < fields.FieldCount; k += 1) {
                            result.Add(fields.get_Field(k).Name, row.Row.get_Value(k));
                        }
                    }
                }
            }
            return result;
        }

        private static char EncodeChar (int value) {
            value += 32;
            if (value >= 34)
                value += 1;
            if (value >= 92)
                value += 1;
            return (char)value;
        }

        private static IEnvelope ClampExtent (IEnvelope extent) {
            EnvelopeClass result = new EnvelopeClass();
            double origin = Math.PI * 6378137;
            result.XMin = Math.Max(extent.XMin, -origin);
            result.YMin = Math.Max(extent.YMin, -origin);
            result.XMax = Math.Min(extent.XMax, origin - 1);
            result.YMax = Math.Min(extent.YMax, origin - 1);
            return result;
        }

        private static Cell MetersToTile (int level, double mx, double my) {
            double resolution = (2 * Math.PI * 6378137.0 / 256.0) / Math.Pow(2, level);
            double origin = Math.PI * 6378137;
            double px = (mx + origin) / resolution;
            double py = (origin - my) / resolution;
            int tx = (int)Math.Floor(px / 256.0);
            int ty = (int)Math.Floor(py / 256.0);
            return new Cell(ty, tx);
        }

        private static IEnumerable<TileDescription> DescribeTiles (int level, IEnvelope extent) {
            extent = ClampExtent(extent);
            double resolution = (2 * Math.PI * 6378137.0 / 256.0) / Math.Pow(2, level);
            double origin = Math.PI * 6378137;
            double tileSizeM = resolution * 256;
            Cell topLeft = MetersToTile(level, extent.XMin, extent.YMax);
            Cell botRight = MetersToTile(level, extent.XMax, extent.YMin);
            for (int x = topLeft.Col; x <= botRight.Col; x += 1) {
                for (int y = topLeft.Row; y <= botRight.Row; y += 1) {
                    double mx = -origin + tileSizeM * x;
                    double my = origin - tileSizeM * y;
                    yield return new TileDescription(y, x, mx, my - tileSizeM, mx + tileSizeM, my);
                }
            }
        }
    }

    internal class Cell
    {
        public readonly int Row;
        public readonly int Col;

        public Cell (int row, int col) {
            Row = row;
            Col = col;
        }

        public override bool Equals (object obj) {
            return (obj is Cell) &&
                   (((Cell)obj).Row == Row) &&
                   (((Cell)obj).Col == Col);
        }

        public override int GetHashCode () {
             unchecked { // Overflow is fine, just wrap
                int hash = 17;
                // Suitable nullity checks etc, of course :)
                hash = hash * 23 + Row;
                hash = hash * 23 + Col;
                return hash;
            }
        }
    }

    internal class TileDescription : Cell
    {
        public readonly IEnvelope Extent;

        public TileDescription (int row, int col, double xmin, double ymin, double xmax, double ymax) 
                : base(row, col) {
            Extent = new EnvelopeClass();
            Extent.XMin = xmin;
            Extent.YMin = ymin;
            Extent.XMax = xmax;
            Extent.YMax = ymax;
        }
    }

    internal class ValueList : SortedList<string, object>
    {
        public override bool Equals (object obj) {
            if (obj is ValueList) {
                IEnumerator<KeyValuePair<string, object>> self = this.GetEnumerator();
                IEnumerator<KeyValuePair<string, object>> other = ((ValueList)obj).GetEnumerator();
                try {
                    while (self.MoveNext() && other.MoveNext()) {
                        if ((!self.Current.Key.Equals(other.Current.Key)) ||
                            (!self.Current.Value.Equals(other.Current.Value))) {
                            return false;
                        }
                    }
                } finally {
                    self.Dispose();
                    other.Dispose();
                }
                return true;
            } else {
                return false;
            }
        }

        public override int GetHashCode () {
            unchecked { // Overflow is fine, just wrap
                int hash = 17;
                foreach (KeyValuePair<string, object> item in this) {
                    hash = hash * 23 + item.Key.GetHashCode();
                    hash = hash * 23 + item.Value.GetHashCode();
                    return hash;
                }
                return hash;
            }
        }
    }
}
