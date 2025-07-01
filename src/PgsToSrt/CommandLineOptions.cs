using CommandLine;

namespace PgsToSrt.Options
{
    public class CommandLineOptions
    {
        [Option('i', "input", Required = true, HelpText = "Input PGS subtitle file or MKV file")]
        public string Input { get; set; }

        [Option('o', "output", Required = false, HelpText = "Output SRT subtitle file")]
        public string Output { get; set; }

        [Option('t', "track", Required = false, HelpText = "Track number for MKV files")]
        public int? Track { get; set; }

        [Option('l', "track-language", Required = false, HelpText = "Track language code (e.g., 'eng', 'jpn') for MKV files")]
        public string TrackLanguage { get; set; }

        [Option("tesseract-data", Required = false, HelpText = "Path to Tesseract data directory")]
        public string TesseractData { get; set; }

        [Option("tesseract-language", Required = false, HelpText = "Tesseract language to use for OCR")]
        public string TesseractLanguage { get; set; }

        [Option("tesseract-version", Required = false, HelpText = "Tesseract version (4 or 5)")]
        public string TesseractVersion { get; set; }

        [Option("liblept-name", Required = false, HelpText = "Custom Leptonica library name")]
        public string LibLeptName { get; set; }

        [Option("liblept-version", Required = false, HelpText = "Leptonica version")]
        public string LibLeptVersion { get; set; }

        [Option("character-blacklist", Required = false, HelpText = "Characters to exclude from OCR")]
        public string CharacterBlacklist { get; set; }

        [Option("short-threshold", Required = false, Default = 300, HelpText = "Minimum duration in milliseconds for short subtitles")]
        public int ShortThreshold { get; set; }

        [Option("extend-to", Required = false, Default = 1200, HelpText = "Extend short subtitles to this duration in milliseconds")]
        public int ExtendTo { get; set; }

        [Option("allow-overlap", Required = false, Default = true, HelpText = "Allow overlapping subtitles for multiple speakers")]
        public bool AllowOverlap { get; set; }

        [Option("position-threshold", Required = false, Default = 50, HelpText = "Y-position difference threshold to identify different speakers (in pixels)")]
        public int PositionThreshold { get; set; }
    }
}