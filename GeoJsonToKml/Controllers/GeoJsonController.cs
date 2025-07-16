using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using SharpKml.Base;
using SharpKml.Dom;
using SharpKml.Engine;
using LinearRing = SharpKml.Dom.LinearRing;
using LineString = SharpKml.Dom.LineString;
using Point = SharpKml.Dom.Point;
using Polygon = SharpKml.Dom.Polygon;

namespace GeoJsonToKml.Controllers
{


    [ApiController]
    [Route("api/convert")]
    public class GeoJsonController : ControllerBase
    {
        [HttpPost("geojson-to-kml")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> ConvertGeoJsonToKml(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded");

            try
            {
                // 1. خواندن فایل GeoJSON
                FeatureCollection featureCollection;
                using (var streamReader = new StreamReader(file.OpenReadStream()))
                {
                    var geoJsonReader = new GeoJsonReader();
                    featureCollection = geoJsonReader.Read<FeatureCollection>(await streamReader.ReadToEndAsync());
                }

                // 2. تبدیل به ساختار KML
                var document = new Document
                {
                    Name = "Converted GeoJSON",
                    Description = new Description { Text = "Converted from GeoJSON file" }
                };

                // اضافه کردن Placemarkها به Document
                foreach (var feature in featureCollection)
                {
                    document.AddFeature(ConvertToPlacemark(feature));
                }

                var kml = new Kml
                {
                    Feature = document
                };

                // 3. تولید فایل خروجی
                using (var memoryStream = new MemoryStream())
                {
                    var kmlFile = KmlFile.Create(kml, false);
                    kmlFile.Save(memoryStream);
                    return File(memoryStream.ToArray(),
                              "application/vnd.google-earth.kml+xml",
                              $"{Path.GetFileNameWithoutExtension(file.FileName)}.kml");
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Conversion error: {ex.Message}");
            }
        }

        [HttpPost("kml-to-geojson")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> ConvertKmlToGeoJson(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded");

            try
            {
                // خواندن و پارس KML
                KmlFile kmlFile;
                using (var stream = file.OpenReadStream())
                {
                    kmlFile = KmlFile.Load(stream);
                }

                // ایجاد FeatureCollection
                var featureCollection = new FeatureCollection();

                // استخراج Placemarkها
                foreach (var placemark in kmlFile.Root.Flatten().OfType<Placemark>())
                {
                    var geometry = ConvertKmlGeometryToNts(placemark.Geometry);
                    var attributes = new AttributesTable();

                    if (!string.IsNullOrEmpty(placemark.Name))
                        attributes.Add("name", placemark.Name);

                    if (placemark.ExtendedData != null)
                    {
                        foreach (var data in placemark.ExtendedData.Data)
                        {
                            attributes.Add(data.Name, data.Value);
                        }
                    }

                    featureCollection.Add(new NetTopologySuite.Features.Feature(geometry, attributes));
                }

                // تولید GeoJSON
                var geoJsonWriter = new GeoJsonWriter();
                var geoJson = geoJsonWriter.Write(featureCollection);

                var bytes = System.Text.Encoding.UTF8.GetBytes(geoJson);
                return File(bytes, "application/json", "output.geojson");
            }
            catch (System.Exception ex)
            {
                return StatusCode(500, $"Error during conversion: {ex.Message}");
            }
        }

        [HttpPost("multi-kml-to-kmz")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> ConvertMultipleKmlToKmz(List<IFormFile> files)
        {
            if (files == null || files.Count == 0)
                return BadRequest("No files uploaded");

            try
            {
                using (var kmzStream = new MemoryStream())
                {
                    using (var archive = new System.IO.Compression.ZipArchive(kmzStream, System.IO.Compression.ZipArchiveMode.Create, true))
                    {
                        int index = 1;

                        foreach (var file in files)
                        {
                            if (Path.GetExtension(file.FileName).ToLower() != ".kml")
                                continue; 

                            var entryName = index == 1 ? "doc.kml" : $"class{index}.kml";
                            var entry = archive.CreateEntry(entryName);

                            using (var entryStream = entry.Open())
                            using (var fileStream = file.OpenReadStream())
                            {
                                await fileStream.CopyToAsync(entryStream);
                            }

                            index++;
                        }
                    }

                    return File(kmzStream.ToArray(), "application/vnd.google-earth.kmz", "merged.kmz");
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error during multi-KML to KMZ conversion: {ex.Message}");
            }
        }



        private Placemark ConvertToPlacemark(IFeature feature)
        {
            return new Placemark
            {
                Name = feature.Attributes?.Exists("name") == true
                       ? feature.Attributes["name"].ToString()
                       : "Unnamed Feature",
                Geometry = ConvertGeometry(feature.Geometry),
                ExtendedData = CreateExtendedData(feature.Attributes)
            };
        }

        private SharpKml.Dom.Geometry ConvertGeometry(NetTopologySuite.Geometries.Geometry geometry)
        {
            switch (geometry)
            {
                case NetTopologySuite.Geometries.Point point:
                    return new Point { Coordinate = new Vector(point.Y, point.X) };

                case NetTopologySuite.Geometries.LineString lineString:
                    return new LineString
                    {
                        Coordinates = new CoordinateCollection(
                            lineString.Coordinates.Select(c => new Vector(c.Y, c.X)))
                    };

                case NetTopologySuite.Geometries.Polygon polygon:
                    var kmlPolygon = new Polygon
                    {
                        OuterBoundary = new OuterBoundary
                        {
                            LinearRing = CreateLinearRing(polygon.ExteriorRing)
                        }
                    };

                    // اضافه کردن حلقه‌های داخلی با استفاده از AddInnerBoundary
                    for (int i = 0; i < polygon.NumInteriorRings; i++)
                    {
                        kmlPolygon.AddInnerBoundary(new InnerBoundary
                        {
                            LinearRing = CreateLinearRing(polygon.GetInteriorRingN(i))
                        });
                    }
                    return kmlPolygon;

                case NetTopologySuite.Geometries.MultiPolygon multiPolygon:
                    var multiGeometry = new MultipleGeometry();
                    foreach (var poly in multiPolygon.Geometries.Cast<NetTopologySuite.Geometries.Polygon>())
                    {
                        multiGeometry.AddGeometry(ConvertGeometry(poly));
                    }
                    return multiGeometry;

                default:
                    throw new NotSupportedException($"Geometry type {geometry.GeometryType} is not supported");
            }
        }

        private LinearRing CreateLinearRing(NetTopologySuite.Geometries.LineString ring)
        {
            return new LinearRing
            {
                Coordinates = new CoordinateCollection(
                    ring.Coordinates.Select(c => new Vector(c.Y, c.X)))
            };
        }

        private ExtendedData CreateExtendedData(IAttributesTable attributes)
        {
            if (attributes == null || attributes.Count == 0)
                return null;

            var extendedData = new ExtendedData();
            foreach (var name in attributes.GetNames())
            {
                extendedData.AddData(new Data
                {
                    Name = name,
                    Value = attributes[name]?.ToString() ?? string.Empty
                });
            }
            return extendedData;
        }



        private NetTopologySuite.Geometries.Geometry ConvertKmlGeometryToNts(SharpKml.Dom.Geometry geometry)
        {
            var factory = NetTopologySuite.Geometries.GeometryFactory.Default;

            switch (geometry)
            {
                case Point point:
                    return factory.CreatePoint(new Coordinate(point.Coordinate.Longitude, point.Coordinate.Latitude));

                case LineString lineString:
                    return factory.CreateLineString(
                        lineString.Coordinates.Select(c => new Coordinate(c.Longitude, c.Latitude)).ToArray());

                case Polygon polygon:
                    var exterior = factory.CreateLinearRing(
                        polygon.OuterBoundary.LinearRing.Coordinates.Select(c => new Coordinate(c.Longitude, c.Latitude)).ToArray());

                    var interiors = polygon.InnerBoundary.Select(inner =>
                        factory.CreateLinearRing(inner.LinearRing.Coordinates.Select(c => new Coordinate(c.Longitude, c.Latitude)).ToArray())
                    ).ToArray();

                    return factory.CreatePolygon(exterior, interiors);

                case MultipleGeometry multiGeometry:
                    var geometries = multiGeometry.Geometry.Select(ConvertKmlGeometryToNts).ToArray();
                    return factory.CreateGeometryCollection(geometries);

                default:
                    throw new NotSupportedException($"KML Geometry {geometry.GetType().Name} is not supported");
            }
        }



    }
}