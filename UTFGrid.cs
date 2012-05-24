using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ESRI.ArcGIS;
using ESRI.ArcGIS.Carto;
using ESRI.ArcGIS.esriSystem;
using ESRI.ArcGIS.Geodatabase;
using ESRI.ArcGIS.Geometry;
using Ionic.Zlib;
using NDesk.Options;
using Newtonsoft.Json;

namespace NatGeo.UTFGrid
{
    class UTFGrid
    {
        [STAThread()]
        static void Main (string[] args) {
            bool showHelp = false;
            bool gzip = false;
            bool overwrite = false;
            string destination = ".";
            int[] levels = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };
            HashSet<string> fields = null;
            int threadCount = System.Environment.ProcessorCount;

            OptionSet p = new OptionSet() {
                { "d|dir=", "destination directory (defaults to current directory)", d => destination = d },
                { "l|levels=", 
                  "list of scale levels [0-19], separated by commas", 
                  l => levels = l.Split(new char[] { ',' }).Select(s => Convert.ToInt32(s)).ToArray() },
                { "f|fields=", 
                  "list of field names to include in UTFGrid data",
                  f => fields = new HashSet<string>(f.Split(new char[] { ',' })) },
                { "t|threads=", 
                  "number of threads to use (defaults to number of processors)",
                  t => threadCount = Convert.ToInt32(t) },
                { "z|zip",  "zip the json files using gzip compression before saving", z => gzip = z != null },
                { "o|overwrite", "overwrite existing files", o => overwrite = o != null },
                { "h|help",  "show this message and exit", h => showHelp = h != null }
            };
            List<string> extra;
            try {
                extra = p.Parse(args);
            } catch (OptionException e) {
                Console.Write("utfgrid");
                Console.WriteLine(e.Message);
                Console.WriteLine("Try `utfgrid --help' for more information.");
                return;
            }
            if (showHelp) {
                Console.WriteLine("Usage: utfgrid [OPTIONS]+ mxd_document");
                Console.WriteLine("Generate UTFGrid files from the given map document");
                Console.WriteLine();
                Console.WriteLine("Options:");
                p.WriteOptionDescriptions(Console.Out);
                return;
            } else if (extra.Count < 1) {
                Console.WriteLine("utfgrid: no map document specified");
                Console.WriteLine("Try `utfgrid --help' for more information.");
                return;
            }
            RuntimeManager.BindLicense(ProductCode.EngineOrDesktop);
            
            IMap map = null;
            try {
                IMapDocument mapDocument = new MapDocumentClass();
                mapDocument.Open(extra[0], null);
                map = mapDocument.ActiveView as IMap;
                if (map == null) {
                    map = mapDocument.get_Map(0);
                }
                mapDocument.Close();
            } catch (Exception) { }
            if (map == null) {
                Console.WriteLine("Unable to open map at " + extra[0]);
                return;
            }
            if ((map.SpatialReference.FactoryCode != 102113) && 
                (map.SpatialReference.FactoryCode != 102100) &&
                (map.SpatialReference.FactoryCode != 3857)) {
                Console.WriteLine("Spatial reference of map must be Web Mercator (is " + map.SpatialReference.FactoryCode + ")");
                return;
            }

            IActiveView activeView = map as IActiveView;
            // get the extent from the active view
            IEnvelope fullExtent = activeView.FullExtent;

            Console.WriteLine(fields);
            foreach (int level in levels)
                Console.WriteLine(level);

            UTFGridGeneratorConfig config = new UTFGridGeneratorConfig(extra[0], DescribeTiles(levels, activeView.FullExtent));
            config.GZip = gzip;
            config.Overwrite = overwrite;
            config.Destination = destination;

            Thread[] workerThreads = new Thread[threadCount];

            for (int i = 0; i < threadCount; i++) {
                workerThreads[i] = new Thread(new ParameterizedThreadStart(UTFGridGenerator.Execute));
                workerThreads[i].SetApartmentState(ApartmentState.STA);
                workerThreads[i].IsBackground = true;
                workerThreads[i].Priority = ThreadPriority.BelowNormal;
                workerThreads[i].Name = "UTFGridGenerator " + (i + 1).ToString();
                Console.WriteLine("starting thread " + workerThreads[i].Name);
                workerThreads[i].Start(config);
            }

            foreach (Thread t in workerThreads) {
                t.Join();
            }
            workerThreads = null;
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

        private static IEnumerable<TileDescription> DescribeTiles (int[] levels, IEnvelope extent) {
            extent = ClampExtent(extent);
            foreach (int level in levels) {
                double resolution = (2 * Math.PI * 6378137.0 / 256.0) / Math.Pow(2, level);
                double origin = Math.PI * 6378137;
                double tileSizeM = resolution * 256;
                Cell topLeft = MetersToTile(level, extent.XMin, extent.YMax);
                Cell botRight = MetersToTile(level, extent.XMax, extent.YMin);
                for (int y = topLeft.Row; y <= botRight.Row; y += 1) {
                    for (int x = topLeft.Col; x <= botRight.Col; x += 1) {
                        double mx = -origin + tileSizeM * x;
                        double my = origin - tileSizeM * y;
                        yield return new TileDescription(level, y, x, mx, my - tileSizeM, mx + tileSizeM, my);
                    }
                }
            }
        }
    }
}
