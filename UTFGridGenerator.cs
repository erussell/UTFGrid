using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ESRI.ArcGIS.Carto;
using ESRI.ArcGIS.esriSystem;
using ESRI.ArcGIS.Geodatabase;
using ESRI.ArcGIS.Geometry;
using Ionic.Zlib;
using Newtonsoft.Json;


namespace NatGeo.UTFGrid
{
    // the thread data which is shared to all processing threads.
    // this class defines mostly properties and will be passed as 
    // a parameter to each thread.
    sealed class UTFGridGeneratorConfig
    {
        public bool GZip { get; set; }
        public bool Overwrite { get; set; }
        public bool Verbose { get; set; }
        public string Destination { get; set; }
        public HashSet<string> Fields { get; set; }
        public string MapPath { get { return m_mapPath; } }
        public int WorkerCount { get { return m_workers; } }

        private string m_mapPath;
        private IEnumerator<TileDescription> m_tiles;
        private int m_workers;

        public UTFGridGeneratorConfig (string mapPath, IEnumerable<TileDescription> tiles) {
            m_mapPath = mapPath;
            m_tiles = tiles.GetEnumerator();
            m_workers = 0;
            GZip = false;
            Overwrite = false;
            Destination = ".";
            Fields = null;
        }

        public TileDescription NextTile () {
            TileDescription result = null;
            lock (m_tiles) {
                if (m_tiles.MoveNext()) {
                    result = m_tiles.Current;
                } else {
                    result = null;
                }
            }
            return result;
        }

        public void WorkerStart () {
            Interlocked.Increment(ref m_workers);
        }

        public void WorkerEnd () {
            Interlocked.Decrement(ref m_workers);
        }
    }
    
    class UTFGridGenerator
    {
        public static void Execute (object threadData) {
            UTFGridGeneratorConfig config = threadData as UTFGridGeneratorConfig;
            try {
                config.WorkerStart();
                IMap map = null;
                try {
                    IMapDocument mapDocument = new MapDocumentClass();
                    mapDocument.Open(config.MapPath, null);
                    map = mapDocument.ActiveView as IMap;
                    if (map == null) {
                        map = mapDocument.get_Map(0);
                    }
                    mapDocument.Close();
                } catch (Exception) { }
                if (map == null) {
                    throw new Exception("Unable to open map at " + config.MapPath);
                }
                if ((map.SpatialReference.FactoryCode != 102113) &&
                    (map.SpatialReference.FactoryCode != 102100) &&
                    (map.SpatialReference.FactoryCode != 3785)) {
                    throw new Exception("Spatial reference of map must be Web Mercator (is " + map.SpatialReference.FactoryCode + ")");
                }

                while (true) {
                    TileDescription tile = config.NextTile();
                    if (tile == null) {
                        return;
                    }
                    string folder = String.Format("{0}\\{1}\\{2}", config.Destination, tile.Level, tile.Col);
                    if (!Directory.Exists(folder)) {
                        Directory.CreateDirectory(folder);
                    }
                    string file = String.Format("{0}\\{1}.grid.json", folder, tile.Row);
                    if (config.GZip) file += ".gz";
                    if ((!File.Exists(file)) || config.Overwrite) {
                        if (config.Verbose) {
                            Console.WriteLine(Thread.CurrentThread.Name + " generating tile " + tile.Level + ", " + tile.Row + ", " + tile.Col);
                        }
                        Dictionary<string, object> data = CollectData(map, tile.Extent, config.Fields);
                        if (data != null) {
                            if (config.Verbose) {
                                Console.WriteLine(Thread.CurrentThread.Name + " saving to " + file);
                            }
                            Stream fOut = new System.IO.FileStream(file, FileMode.Create);
                            if (config.GZip) {
                                fOut = new GZipStream(fOut, CompressionMode.Compress, CompressionLevel.BestCompression);
                            }
                            using (fOut) {
                                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                                Encoding utf8 = new UTF8Encoding(false, true);
                                byte[] encodedJson = utf8.GetBytes(json);
                                fOut.Write(encodedJson, 0, encodedJson.Length);
                            }
                        }
                    }
                }
            } finally {
                config.WorkerEnd();
            }
        }
        
        private static Dictionary<string, object> CollectData (IMap map, IEnvelope extent, HashSet<string> includeFields) {
            const int tileSize = 128;
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
                    ValueList cellData = GetPixelData(map, pixelExtent, includeFields);
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
            if (keys.Count > 1) {
                Dictionary<string, object> result = new Dictionary<string, object>();
                result.Add("grid", grid.Select(sb => sb.ToString()).ToArray());
                result.Add("keys", keys.ToArray());
                result.Add("data", data);
                return result;
            } else {
                return null;
            }
        }

        private static ValueList GetPixelData (IMap map, IEnvelope extent, HashSet<string> includeFields) {
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
                if (data != null) {
                    for (int j = 0; j < data.Count; j += 1) {
                        object foundObj = data.get_Element(j);
                        IRasterIdentifyObj2 raster = foundObj as IRasterIdentifyObj2;
                        IRowIdentifyObject row = foundObj as IRowIdentifyObject;
                        if (raster != null) {
                            int propertyIndex = 0;
                            string property;
                            string value;
                            while (true) {
                                try {
                                    raster.GetPropAndValues(propertyIndex, out property, out value);
                                    if ((!"NoData".Equals(value)) &&
                                        ((includeFields == null) || includeFields.Contains(property))) {
                                        result.Add(property, value);
                                    }
                                    propertyIndex += 1;
                                } catch {
                                    break;
                                }
                            }
                            continue;
                        } else if (row != null) {
                            IFields fields = row.Row.Fields;
                            for (int k = 0; k < fields.FieldCount; k += 1) {
                                string fieldName = fields.get_Field(k).Name;
                                if ((includeFields == null) ? (!result.ContainsKey(fieldName)) : includeFields.Contains(fieldName)) {
                                    result.Add(fieldName, row.Row.get_Value(k));
                                }
                            }
                        }
                    }
                    break;
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
        public readonly int Level;
        public readonly IEnvelope Extent;

        public TileDescription (int level, int row, int col, double xmin, double ymin, double xmax, double ymax)
            : base(row, col) {
            Level = level;
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

