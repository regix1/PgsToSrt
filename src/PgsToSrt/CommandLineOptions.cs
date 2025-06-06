using CommandLine;

namespace PgsToSrt
{
    internal class CommandLineOptions
    {
        [Option(Required = true, HelpText = "Input filename, it can be a .mkv or a .sup extracted with mkvextract.")]
        public string Input { get; set; }

        [Option(HelpText = "Track language.")]
        public string TrackLanguage { get; set; }

        [Option(HelpText = "Track number of the PGS subtitle to use, only needed when input is an .mkv file.")]
        public int? Track { get; set; }

        [Option(Required = false, HelpText = "Output .srt filename.")]
        public string Output { get; set; }

        [Option(HelpText = "Tesseract language to use if multiple languages are available in the tesseract data directory.")]
        public string TesseractLanguage { get; set; }

        [Option(HelpText = "Path of tesseract language data files, by default 'tessdata' in the executable directory.")]
        public string TesseractData { get; set; }

        [Option(HelpText = $"Tesseract version", Default = Runner.DefaultTesseractVersion)]
        public string TesseractVersion { get; set; }

        [Option(Required = false, HelpText = "Leptonica library name.", Default = "lept")]
        public string LibLeptName { get; set; }

        [Option(Required = false, HelpText = "Leptonica library version.", Default = "5")]
        public string LibLeptVersion { get; set; }

        [Option('b', "blacklist", Required = false, HelpText = "Character blacklist to improve OCR (e.g., \"|\\/`_~<>\")")]
        public string CharacterBlacklist { get; set; }

        [Option("short-threshold", Required = false, HelpText = "Duration threshold - subtitles shorter than this will be extended (default: 300ms)", Default = 300)]
        public int ShortThreshold { get; set; }

        [Option("extend-to", Required = false, HelpText = "Duration to extend short subtitles to in milliseconds (default: 1200ms)", Default = 1200)]
        public int ExtendTo { get; set; }
    }
}