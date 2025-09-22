using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using ParadoxParser.CSV;
using ParadoxParser.Utilities;

namespace ParadoxParser.Bitmap
{
    /// <summary>
    /// High-performance province map parser that combines BMP parsing with province definitions
    /// Maps RGB colors to province IDs using definition.csv data
    /// </summary>
    public static class ProvinceMapParser
    {
        /// <summary>
        /// Province map parsing result
        /// </summary>
        public struct ProvinceMapResult
        {
            public BMPParser.BMPPixelData PixelData;
            public NativeHashMap<int, int> ColorToProvinceID; // RGB -> Province ID
            public NativeHashMap<int, int> ProvinceIDToColor; // Province ID -> RGB
            public NativeArray<int> UniqueProvinceIDs;
            public int ProvinceCount;
            public bool Success;

            public void Dispose()
            {
                PixelData.Dispose();
                if (ColorToProvinceID.IsCreated) ColorToProvinceID.Dispose();
                if (ProvinceIDToColor.IsCreated) ProvinceIDToColor.Dispose();
                if (UniqueProvinceIDs.IsCreated) UniqueProvinceIDs.Dispose();
            }
        }

        /// <summary>
        /// Parse province map by combining BMP data with definition CSV
        /// </summary>
        public static ProvinceMapResult ParseProvinceMap(
            NativeArray<byte> bmpFileData,
            NativeArray<byte> definitionCsvData,
            Allocator allocator)
        {
            return ParseProvinceMap(new NativeSlice<byte>(bmpFileData), new NativeSlice<byte>(definitionCsvData), allocator);
        }

        /// <summary>
        /// Parse province map by combining BMP data with definition CSV
        /// </summary>
        public static ProvinceMapResult ParseProvinceMap(
            NativeSlice<byte> bmpFileData,
            NativeSlice<byte> definitionCsvData,
            Allocator allocator)
        {
            // Parse BMP header
            var bmpHeader = BMPParser.ParseHeader(bmpFileData);
            if (!bmpHeader.IsValid)
            {
                return new ProvinceMapResult { Success = false };
            }

            // Get pixel data
            var pixelData = BMPParser.GetPixelData(bmpFileData, bmpHeader);
            if (!pixelData.Success)
            {
                return new ProvinceMapResult { Success = false };
            }

            // Parse definition CSV
            var csvResult = CSVParser.Parse(definitionCsvData, Allocator.Temp, hasHeader: true);
            if (!csvResult.Success)
            {
                pixelData.Dispose();
                return new ProvinceMapResult { Success = false };
            }

            try
            {
                // Build color mappings from CSV
                var colorToProvinceID = BuildColorMappings(csvResult, allocator, out var provinceIDToColor, out var uniqueProvinceIDs, out int provinceCount);

                return new ProvinceMapResult
                {
                    PixelData = pixelData,
                    ColorToProvinceID = colorToProvinceID,
                    ProvinceIDToColor = provinceIDToColor,
                    UniqueProvinceIDs = uniqueProvinceIDs,
                    ProvinceCount = provinceCount,
                    Success = true
                };
            }
            finally
            {
                csvResult.Dispose();
            }
        }

        /// <summary>
        /// Get province ID at specific pixel coordinates
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetProvinceAt(ProvinceMapResult mapResult, int x, int y, out int provinceID)
        {
            provinceID = -1;

            if (!mapResult.Success)
                return false;

            if (BMPParser.TryGetPixelRGBPacked(mapResult.PixelData, x, y, out int rgb))
            {
                return mapResult.ColorToProvinceID.TryGetValue(rgb, out provinceID);
            }

            return false;
        }

        /// <summary>
        /// Find all pixels belonging to a specific province
        /// </summary>
        public static NativeList<PixelCoord> FindProvincePixels(ProvinceMapResult mapResult, int provinceID, Allocator allocator)
        {
            var pixels = new NativeList<PixelCoord>(1000, allocator);

            if (!mapResult.Success)
                return pixels;

            // Get the RGB color for this province
            if (!mapResult.ProvinceIDToColor.TryGetValue(provinceID, out int targetRGB))
                return pixels;

            // Find all pixels with this color
            return BMPParser.FindPixelsWithColor(mapResult.PixelData, targetRGB, allocator);
        }

        /// <summary>
        /// Get province statistics (pixel count, bounding box, etc.)
        /// </summary>
        public struct ProvinceStats
        {
            public int ProvinceID;
            public int PixelCount;
            public int MinX, MinY, MaxX, MaxY; // Bounding box
            public PixelCoord Centroid; // Average position
            public bool IsValid;

            public int Width => MaxX - MinX + 1;
            public int Height => MaxY - MinY + 1;
            public int Area => PixelCount; // In pixels
        }

        /// <summary>
        /// Calculate statistics for a province
        /// </summary>
        public static ProvinceStats CalculateProvinceStats(ProvinceMapResult mapResult, int provinceID)
        {
            if (!mapResult.Success)
                return new ProvinceStats { IsValid = false };

            using var pixels = FindProvincePixels(mapResult, provinceID, Allocator.Temp);

            if (pixels.Length == 0)
                return new ProvinceStats { IsValid = false };

            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;
            long sumX = 0, sumY = 0;

            for (int i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                minX = Math.Min(minX, pixel.x);
                minY = Math.Min(minY, pixel.y);
                maxX = Math.Max(maxX, pixel.x);
                maxY = Math.Max(maxY, pixel.y);
                sumX += pixel.x;
                sumY += pixel.y;
            }

            return new ProvinceStats
            {
                ProvinceID = provinceID,
                PixelCount = pixels.Length,
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY,
                Centroid = new PixelCoord((int)(sumX / pixels.Length), (int)(sumY / pixels.Length)),
                IsValid = true
            };
        }

        /// <summary>
        /// Build color mappings from CSV definition data
        /// Expected CSV format: province;red;green;blue;name;x
        /// </summary>
        private static NativeHashMap<int, int> BuildColorMappings(
            CSVParser.CSVParseResult csvResult,
            Allocator allocator,
            out NativeHashMap<int, int> provinceIDToColor,
            out NativeArray<int> uniqueProvinceIDs,
            out int provinceCount)
        {
            var colorToProvinceID = new NativeHashMap<int, int>(csvResult.RowCount, allocator);
            provinceIDToColor = new NativeHashMap<int, int>(csvResult.RowCount, allocator);
            var provinceIDList = new NativeList<int>(csvResult.RowCount, Allocator.Temp);

            try
            {
                // Find column indices
                var provinceHash = FastHasher.HashFNV1a32("province");
                var redHash = FastHasher.HashFNV1a32("red");
                var greenHash = FastHasher.HashFNV1a32("green");
                var blueHash = FastHasher.HashFNV1a32("blue");

                int provinceCol = CSVParser.FindColumnIndex(csvResult.HeaderHashes, provinceHash);
                int redCol = CSVParser.FindColumnIndex(csvResult.HeaderHashes, redHash);
                int greenCol = CSVParser.FindColumnIndex(csvResult.HeaderHashes, greenHash);
                int blueCol = CSVParser.FindColumnIndex(csvResult.HeaderHashes, blueHash);

                // Validate required columns exist
                if (provinceCol < 0 || redCol < 0 || greenCol < 0 || blueCol < 0)
                {
                    uniqueProvinceIDs = new NativeArray<int>(0, allocator);
                    provinceCount = 0;
                    return colorToProvinceID;
                }

                // Process each row
                for (int i = 0; i < csvResult.RowCount; i++)
                {
                    var row = csvResult.Rows[i];

                    // Parse province ID and RGB values
                    if (CSVParser.TryGetInt(row, provinceCol, out int provinceID) &&
                        CSVParser.TryGetInt(row, redCol, out int red) &&
                        CSVParser.TryGetInt(row, greenCol, out int green) &&
                        CSVParser.TryGetInt(row, blueCol, out int blue))
                    {
                        // Validate RGB ranges
                        if (red >= 0 && red <= 255 && green >= 0 && green <= 255 && blue >= 0 && blue <= 255)
                        {
                            int packedRGB = (red << 16) | (green << 8) | blue;

                            // Add mappings (handle potential duplicates)
                            colorToProvinceID.TryAdd(packedRGB, provinceID);
                            provinceIDToColor.TryAdd(provinceID, packedRGB);
                            provinceIDList.Add(provinceID);
                        }
                    }
                }

                // Convert province ID list to array
                uniqueProvinceIDs = new NativeArray<int>(provinceIDList.Length, allocator);
                for (int i = 0; i < provinceIDList.Length; i++)
                {
                    uniqueProvinceIDs[i] = provinceIDList[i];
                }

                provinceCount = provinceIDList.Length;
                return colorToProvinceID;
            }
            finally
            {
                provinceIDList.Dispose();
            }
        }

        /// <summary>
        /// Validate province map integrity
        /// Checks for unmapped colors, duplicate colors, etc.
        /// </summary>
        public static ProvinceMapValidationResult ValidateProvinceMap(ProvinceMapResult mapResult, Allocator allocator)
        {
            if (!mapResult.Success)
                return new ProvinceMapValidationResult { IsValid = false };

            var unmappedColors = new NativeList<int>(100, allocator);
            var mappedColors = new NativeHashSet<int>(1000, allocator);

            int width = mapResult.PixelData.Header.Width;
            int height = mapResult.PixelData.Header.Height;
            int totalPixels = 0;
            int mappedPixels = 0;

            // Scan all pixels
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    totalPixels++;

                    if (BMPParser.TryGetPixelRGBPacked(mapResult.PixelData, x, y, out int rgb))
                    {
                        if (mapResult.ColorToProvinceID.ContainsKey(rgb))
                        {
                            mappedPixels++;
                            mappedColors.Add(rgb);
                        }
                        else
                        {
                            if (!unmappedColors.Contains(rgb))
                                unmappedColors.Add(rgb);
                        }
                    }
                }
            }

            return new ProvinceMapValidationResult
            {
                IsValid = unmappedColors.Length == 0,
                TotalPixels = totalPixels,
                MappedPixels = mappedPixels,
                UnmappedPixels = totalPixels - mappedPixels,
                UnmappedColors = unmappedColors,
                MappedColorCount = mappedColors.Count,
                UnmappedColorCount = unmappedColors.Length
            };
        }

        /// <summary>
        /// Province map validation result
        /// </summary>
        public struct ProvinceMapValidationResult
        {
            public bool IsValid;
            public int TotalPixels;
            public int MappedPixels;
            public int UnmappedPixels;
            public NativeList<int> UnmappedColors;
            public int MappedColorCount;
            public int UnmappedColorCount;

            public float MappingCoverage => TotalPixels > 0 ? (float)MappedPixels / TotalPixels : 0f;

            public void Dispose()
            {
                if (UnmappedColors.IsCreated) UnmappedColors.Dispose();
            }
        }
    }
}