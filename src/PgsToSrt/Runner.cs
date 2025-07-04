using CommandLine;
using Microsoft.Extensions.Logging;
using PgsToSrt.Options;
using PgsToSrt.BluRaySup;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace PgsToSrt
{
    internal class Runner
    {
        public const string DefaultTesseractVersion = "4";

        private readonly string[] _tesseractSupportedVersions = ["4", "5"];
        private readonly ILogger _logger;

        private string _tesseractData;
        private string _tesseractLanguage;
        private string _tesseractVersion = DefaultTesseractVersion;
        private string _libLeptName;
        private string _libLeptVersion;
        private string _characterBlacklist;
        private int _shortThreshold;
        private int _extendTo;
        private bool _allowOverlap;
        private int _positionThreshold;

        public Runner(ILogger<Runner> logger)
        {
            _logger = logger;
        }

        public void Run(Parsed<CommandLineOptions> values)
        {
            if (values != null)
            {
                var (argumentChecked, runnerOptions) = GetTrackOptions(values);

                if (argumentChecked)
                {
                    foreach (var runnerOption in runnerOptions)
                    {
                        ConvertPgs(runnerOption.Input, runnerOption.Track, runnerOption.Output);
                    }
                }
            }
        }

        private (bool result, List<TrackOption> trackOptions) GetTrackOptions(Parsed<CommandLineOptions> values)
        {
            var result = true;
            var trackOptions = new List<TrackOption>();
            var input = values.Value.Input;
            var output = values.Value.Output;
            var trackLanguage = values.Value.TrackLanguage;
            var track = values.Value.Track;

            _characterBlacklist = values.Value.CharacterBlacklist;
            _shortThreshold = values.Value.ShortThreshold;
            _extendTo = values.Value.ExtendTo;
            _allowOverlap = values.Value.AllowOverlap;
            _positionThreshold = values.Value.PositionThreshold;

            // Windows uses tesseract50.dll installed by nuget package, so always use v5
            // Other systems can uses different libtesseract versions, keep v4 as default.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (values.Value.TesseractVersion != null)
                {
                    _tesseractVersion = values.Value.TesseractVersion;

                    if (!_tesseractSupportedVersions.Contains(_tesseractVersion))
                    {
                        _logger.LogError($"Unsupported Tesseract version '{_tesseractVersion}' (Supported versions: 4, 5)");
                        result = false;
                    }
                }

                _libLeptName = values.Value.LibLeptName;
                _libLeptVersion = values.Value.LibLeptVersion;
            }
            else
            {
                _tesseractVersion = "5";
            }

            _tesseractData = !string.IsNullOrEmpty(values.Value.TesseractData)
                ? values.Value.TesseractData
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

            if (!File.Exists(values.Value.Input))
            {
                _logger.LogError($"Input file '{input}' doesn't exist.");
                result = false;
            }

            if (MkvUtilities.IsMkvFile(input))
            {
                if (string.IsNullOrEmpty(trackLanguage) && !track.HasValue)
                {
                    _logger.LogError("Track must be set when input is an mkv/s file.");
                    result = false;
                }
                else if (!string.IsNullOrEmpty(trackLanguage))
                {
                    var runnerOptionLanguages = MkvUtilities.GetTracksByLanguage(input, trackLanguage, output);
                    trackOptions.AddRange(runnerOptionLanguages.Select(item => new TrackOption() { Input = input, Output = item.Output, Track = item.Track }));
                }
                else
                {
                    trackOptions.Add(new TrackOption() {Input = input, Output = output, Track = track});
                }
            }
            else
            {
                var outputFilename = !string.IsNullOrEmpty(output) ? output : MkvUtilities.GetBaseDefaultOutputFilename(input, output) + ".srt";

                trackOptions.Add(new TrackOption() {Input = input, Output = outputFilename, Track = null});
            }

            if (Directory.Exists(_tesseractData))
            {
                var tesseractData = new TesseractData(_logger);
                _tesseractLanguage = tesseractData.GetTesseractLanguage(_tesseractData, values.Value.TesseractLanguage);

                if (string.IsNullOrEmpty(_tesseractLanguage))
                {
                    result = false;
                }
            }
            else
            {
                _logger.LogError($"Tesseract data directory '{_tesseractData}' doesn't exist.");
                result = false;
            }

            return (result, trackOptions);
        }

        private bool ConvertPgs(string input, int? track, string output)
        {
            // Set global parser options
            BluRaySupParserImageSharp.AllowOverlap = _allowOverlap;
            BluRaySupParserImageSharp.PositionThreshold = _positionThreshold;

            var pgsParser = new PgsParser(_logger);
            var subtitles = pgsParser.Load(input, track.GetValueOrDefault());

            if (subtitles is null)
                return false;

            var pgsOcr = new PgsOcr(_logger, _tesseractVersion, _libLeptName, _libLeptVersion)
            {
                TesseractDataPath = _tesseractData,
                TesseractLanguage = _tesseractLanguage,
                CharacterBlacklist = _characterBlacklist,
                ShortThreshold = _shortThreshold,
                ExtendTo = _extendTo,
                AllowOverlap = _allowOverlap,
                PositionThreshold = _positionThreshold
            };

            pgsOcr.ToSrt(subtitles, output);

            return true;
        }
        
        private class TrackOption
        {
            public string Input { get; set; }
            public string Output { get; set; }
            public int? Track { get; set; }
        }
    }
}