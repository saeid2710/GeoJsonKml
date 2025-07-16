using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using SharpKml.Base;
using SharpKml.Dom;
using SharpKml.Engine;
using Geometry = NetTopologySuite.Geometries.Geometry;

namespace GeoJsonToKml.Controllers
{
    [ApiController]
    [Route("api/validation")]
    public class AreaValidationController : Controller
    {
        private const double TolerancePercent = 0.05; 
        [HttpPost("check-area-and-location")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> CheckAreaAndLocation(IFormFile mainFile, List<IFormFile> positionFiles)
        {
            if (mainFile == null || positionFiles == null || positionFiles.Count == 0)
                return BadRequest("فایل اصلی یا فایل‌های موقعیت ارسال نشده‌اند.");

            try
            {
                // خواندن فایل اصلی
                var mainGeometry = await ReadGeometryFromFile(mainFile);

                if (mainGeometry == null)
                    return BadRequest("فایل اصلی معتبر نیست.");

                // خواندن و ترکیب فایل‌های موقعیت
                var positionGeometries = new List<Geometry>();

                foreach (var file in positionFiles)
                {
                    var geometries = await ExtractGeometriesFromCompressedFile(file);
                    positionGeometries.AddRange(geometries);
                }

                if (positionGeometries.Count == 0)
                    return BadRequest("هیچ موقعیتی در فایل‌های بارگذاری شده پیدا نشد.");

                var factory = new GeometryFactory();
                var unionedPositions = factory.BuildGeometry(positionGeometries).Union();

                // بررسی مساحت
                double mainArea = mainGeometry.Area;
                double positionsArea = unionedPositions.Area;
                double areaTolerance = mainArea * TolerancePercent;

                bool isAreaOk = Math.Abs(mainArea - positionsArea) <= areaTolerance;

                // بررسی Bounding Box
                var mainEnv = mainGeometry.EnvelopeInternal;
                var posEnv = unionedPositions.EnvelopeInternal;

                bool isEnvOk =
                    Math.Abs(mainEnv.MinX - posEnv.MinX) <= areaTolerance &&
                    Math.Abs(mainEnv.MinY - posEnv.MinY) <= areaTolerance &&
                    Math.Abs(mainEnv.MaxX - posEnv.MaxX) <= areaTolerance &&
                    Math.Abs(mainEnv.MaxY - posEnv.MaxY) <= areaTolerance;

                // خروجی پیام
                if (isAreaOk && isEnvOk)
                    return Ok("✔️ بررسی موفق: مساحت و موقعیت‌ها با فایل اصلی تطابق دارند.");

                var errors = new List<string>();
                if (!isAreaOk)
                    errors.Add($"❌ اختلاف در مساحت: مساحت اصلی={mainArea.ToString(CultureInfo.InvariantCulture)}،{Environment.NewLine} مجموع موقعیت‌ها={positionsArea.ToString(CultureInfo.InvariantCulture)}{Environment.NewLine}");
                if (!isEnvOk)
                    errors.Add("❌ محدوده موقعیت‌ها با فایل اصلی هم‌پوشانی ندارد.");

                return BadRequest(string.Join(" | ", errors));
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"خطا در پردازش: {ex.Message}");
            }
        }

        private async Task<Geometry> ReadGeometryFromFile(IFormFile file)
        {
            var extension = Path.GetExtension(file.FileName).ToLower();
            if (extension == ".kml")
                return await ReadGeometryFromKml(file);
            else if (extension == ".geojson" || extension == ".json")
                return await ReadGeometryFromGeoJson(file);
            else
                throw new NotSupportedException("فایل اصلی باید KML یا GeoJSON باشد.");
        }

        private async Task<Geometry> ReadGeometryFromKml(IFormFile file)
        {
            using (var stream = file.OpenReadStream())
            {
                var parser = new Parser();
                parser.Parse(stream);

                var kml = parser.Root as Kml;
                if (kml?.Feature is Document doc)
                {
                    var geometries = new List<Geometry>();
                    foreach (var feature in doc.Features)
                    {
                        if (feature is Placemark placemark && placemark.Geometry != null)
                        {
                            var geo = KmlGeometryToNts(placemark.Geometry);
                            if (geo != null) geometries.Add(geo);
                        }
                    }

                    var factory = new GeometryFactory();
                    return factory.BuildGeometry(geometries).Union();
                }

                return null;
            }
        }

        private async Task<Geometry> ReadGeometryFromGeoJson(IFormFile file)
        {
            using (var reader = new StreamReader(file.OpenReadStream()))
            {
                var geoJson = await reader.ReadToEndAsync();
                var geoJsonReader = new GeoJsonReader();
                var featureCollection = geoJsonReader.Read<FeatureCollection>(geoJson);

                var geometries = featureCollection.Select(f => f.Geometry).ToList();

                var factory = new GeometryFactory();
                return factory.BuildGeometry(geometries).Union();
            }
        }

        private async Task<List<Geometry>> ExtractGeometriesFromCompressedFile(IFormFile file)
        {
            var geometries = new List<Geometry>();

            using (var archive = new ZipArchive(file.OpenReadStream()))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var stream = entry.Open())
                        {
                            var parser = new Parser();
                            parser.Parse(stream);

                            var kml = parser.Root as Kml;
                            if (kml?.Feature is Document doc)
                            {
                                foreach (var feature in doc.Features)
                                {
                                    if (feature is Placemark placemark && placemark.Geometry != null)
                                    {
                                        var geo = KmlGeometryToNts(placemark.Geometry);
                                        if (geo != null) geometries.Add(geo);
                                    }
                                }
                            }
                        }
                    }
                    else if (entry.FullName.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var stream = entry.Open())
                        using (var reader = new StreamReader(stream))
                        {
                            var geoJson = await reader.ReadToEndAsync();
                            var geoJsonReader = new GeoJsonReader();
                            var features = geoJsonReader.Read<FeatureCollection>(geoJson);

                            geometries.AddRange(features.Select(f => f.Geometry));
                        }
                    }
                }
            }

            return geometries;
        }

        private Geometry KmlGeometryToNts(SharpKml.Dom.Geometry geometry)
        {
            var factory = new GeometryFactory();

            switch (geometry)
            {
                case SharpKml.Dom.Point pt:
                    return factory.CreatePoint(new Coordinate(pt.Coordinate.Longitude, pt.Coordinate.Latitude));

                case SharpKml.Dom.LineString line:
                    var coords = line.Coordinates.Select(c => new Coordinate(c.Longitude, c.Latitude)).ToArray();
                    return factory.CreateLineString(coords);

                case SharpKml.Dom.Polygon poly:
                    var outer = poly.OuterBoundary.LinearRing.Coordinates.Select(c => new Coordinate(c.Longitude, c.Latitude)).ToArray();
                    var shell = factory.CreateLinearRing(outer);

                    var holes = poly.InnerBoundary?.Select(h => factory.CreateLinearRing(
                        h.LinearRing.Coordinates.Select(c => new Coordinate(c.Longitude, c.Latitude)).ToArray()
                    )).ToArray();

                    return factory.CreatePolygon(shell, holes);

                case SharpKml.Dom.MultipleGeometry multi:
                    var geoms = multi.Geometry.Select(KmlGeometryToNts).ToArray();
                    return factory.BuildGeometry(geoms);

                default:
                    return null;
            }
        }

    }
}
