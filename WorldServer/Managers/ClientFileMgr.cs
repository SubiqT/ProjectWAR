using Common;
using FrameWork;
using FrameWork.Misc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WorldServer.Services.World;

namespace WorldServer.Managers
{
    public struct AreaInfluence
    {
        public ushort AreaNumber;
        public byte Realm;
        public ushort InfluenceId;
    }

    /*
    public class MapPiece
    {
        public byte Id;
        public ushort ZoneId;
        public ushort PositionX, PositionY;
        public ushort SizeX, SizeY;
        public Color[,] Colors;
        public BitArray[] PieceMap { get; set; }
        public Zone_Area Area;

        public bool IsPvp(byte realm)
        {
            if (!Program.Config.OpenRvR && Area != null && Area.Realm != 0)
                return false;

            return true;
        }

        public bool IsRvR()
        {
            if (Area != null && Area.Realm == 0)
                return true;

            return false;
        }

        public bool IsOn(ushort pinX, ushort pinY, ushort zoneId)
        {
            if (ZoneId != zoneId)
                return false;

            if (pinX >= PositionX && pinX < PositionX + SizeX)
            {
                if (pinY >= PositionY && pinY < PositionY + SizeY && PieceMap[pinX - PositionX][pinY - PositionY])
                    return true;
            }

            return false;
        }

        public override string ToString()
        {
            return "Id:" + Id + ",Area:" + Area;
        }
    }

    */

    public class ClientZoneInfo
    {
        public ushort ZoneId;
        public string Folder;
        public List<AreaInfluence> Influences;
        public List<Zone_Area> Areas;
        public List<PQuest_Info> PQAreas;
        public byte[,] AreaPixels = new byte[1024, 1024];
        public byte[,] PQAreaPixels = new byte[1024, 1024];

        public ClientZoneInfo(ushort zoneId)
        {
            ZoneId = zoneId;
            Influences = new List<AreaInfluence>();
            Folder = Core.Config.ZoneFolder + "zone" + string.Format("{0:000}", zoneId) + "/";
            Areas = ZoneService.GetZoneAreas(zoneId).OrderBy(area => area.PieceId).ToList();

            try
            {
                LoadInfluences();
                LoadAreaMap();
                LoadPQAreaMap();
            }
            catch (Exception e)
            {
                Log.Error("ClientFile", e.ToString());
            }
        }

        public void LoadAreaMap()
        {
            LoadAreaOverlay(Path.Combine(Folder, "areas" + $"{ZoneId:000}" + ".png"), AreaPixels);
        }

        // Uses a 1024x1024 PNG colour overlay to define a PQ area. The colour must
        // be different for each PQ for the PQ to function correctly.
        public void LoadPQAreaMap()
        {
            LoadAreaOverlay(Path.Combine(Folder, "pqarea" + $"{ZoneId:000}" + ".png"), PQAreaPixels);
        }

        /// <summary>
        /// Reads a zone overlay bitmap, folding the red and green channels into the
        /// single area identifier the world uses.
        /// </summary>
        private static void LoadAreaOverlay(string filePath, byte[,] pixels)
        {
            if (!File.Exists(filePath))
                return;

            PngImage map = PngImage.Load(filePath);

            int width = Math.Min(map.Width, pixels.GetLength(0));
            int height = Math.Min(map.Height, pixels.GetLength(1));

            for (int x = 0; x < width; ++x)
            {
                for (int y = 0; y < height; ++y)
                    pixels[x, y] = (byte)(1 + (map.GetRed(x, y) >> 4) + (map.GetGreen(x, y) >> 4));
            }
        }

        public void LoadInfluences()
        {
            string filePath = Path.Combine(Folder, "influenceids.csv");
            if (!File.Exists(filePath))
                return;

            using (FileStream stream = File.OpenRead(filePath))
            {
                using (StreamReader reader = new StreamReader(stream))
                {
                    reader.ReadLine();
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] datas = line.Split(',');
                        AreaInfluence area = new AreaInfluence
                        {
                            AreaNumber = ushort.Parse(datas[0]),
                            Realm = byte.Parse(datas[1]),
                            InfluenceId = ushort.Parse(datas[2])
                        };
                        Influences.Add(area);
                    }
                }
            }
        }

        public Zone_Area GetZoneAreaFor(ushort pinX, ushort pinY, ushort zoneId, ushort pinz = 0)
        {
            byte areaId = AreaPixels[pinX >> 6, pinY >> 6];
            // Log.Error("areaid", "    " + areaId);
            // fix for black craig keep in the dungeon
            if (ZoneId == 3 && areaId > 20)
            {
                if (pinz < 8394)
                    areaId = 3;
                else
                    areaId -= 15;
            }
            if (Areas == null)
                return null;
            foreach (Zone_Area info in Areas)
                if (info.PieceId == areaId)
                    return info;
            return null;
        }

        public byte GetPQAreaFor(ushort pinX, ushort pinY, ushort zoneId)
        {
            return PQAreaPixels[pinX >> 6, pinY >> 6];
        }
    }

    public class HeightMapInfo
    {
        /// <summary>Heightmaps are sampled on a 64 unit grid.</summary>
        private const int PinsPerSample = 64;

        public HeightMapInfo(int zoneID)
        {
            ZoneID = zoneID;
        }

        public int ZoneID;

        private byte[] _offset;
        private byte[] _terrain;
        private int _width;
        private int _height;
        private bool _loaded;

        public int GetHeight(int pinX, int pinY)
        {
            Load();

            if (_offset == null || _terrain == null)
                return -1;

            int x = pinX / PinsPerSample;
            int y = pinY / PinsPerSample;

            if (x < 0 || x >= _width || y < 0 || y >= _height)
                return -1;

            int sample = y * _width + x;

            // The offset map carries the coarse height, the terrain map the remainder.
            float zValue = (_offset[sample] * 31) + _terrain[sample];

            return (int)(zValue * 16) - 30;
        }

        public void Load()
        {
            if (_loaded)
                return;

            _loaded = true;

            string folder = Core.Config.ZoneFolder + "zone" + string.Format("{0:000}", ZoneID) + "/";

            try
            {
                PngImage offset = PngImage.Load(Path.Combine(folder, "offset.png"));
                PngImage terrain = PngImage.Load(Path.Combine(folder, "terrain.png"));

                if (offset.Width != terrain.Width || offset.Height != terrain.Height)
                    throw new InvalidDataException($"Offset map is {offset.Width}x{offset.Height} but terrain map is {terrain.Width}x{terrain.Height}.");

                _width = offset.Width;
                _height = offset.Height;
                _offset = offset.ExtractChannel(0);
                _terrain = terrain.ExtractChannel(0);
            }
            catch (Exception e)
            {
                Log.Error("HeightMap", "[" + ZoneID + "] Invalid HeightMap \n " + e);
            }
        }
    }

    public static class ClientFileMgr
    {
        #region HeightMap Images

        public static Dictionary<int, HeightMapInfo> Heights = new Dictionary<int, HeightMapInfo>();

        public static int GetHeight(int zoneID, int pinX, int pinY)
        {
            HeightMapInfo info;
            if (!Heights.TryGetValue(zoneID, out info))
            {
                Log.Success("HeightMap", "[" + zoneID + "] Loading Height Map..");
                info = new HeightMapInfo(zoneID);
                Heights.Add(zoneID, info);
            }

            return info.GetHeight(pinX, pinY) / 2;
        }

        #endregion HeightMap Images

        #region MapPiece and CSV

        public static Dictionary<ushort, ClientZoneInfo> ClientZoneFiles = new Dictionary<ushort, ClientZoneInfo>();

        public static ClientZoneInfo GetZoneInfo(ushort zoneId)
        {
            ClientZoneInfo info;
            lock (ClientZoneFiles)
            {
                if (!ClientZoneFiles.TryGetValue(zoneId, out info))
                {
                    info = new ClientZoneInfo(zoneId);
                    ClientZoneFiles.Add(zoneId, info);
                }
            }
            return info;
        }

        #endregion MapPiece and CSV
    }
}