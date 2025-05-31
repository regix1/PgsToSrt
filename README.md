# PgsToSrt

Convert [PGS](https://en.wikipedia.org/wiki/Presentation_Graphic_Stream) subtitles to [SRT](https://en.wikipedia.org/wiki/SubRip) using [OCR](https://en.wikipedia.org/wiki/Optical_character_recognition).

## Prerequisites

- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Tesseract 4 language data files](https://github.com/tesseract-ocr/tessdata/)

Data files must be placed in the `tessdata` folder inside PgsToSrt folder, or the path can be specified in the command line with the --tesseractdata parameter.

You only need data files for the language(s) you want to convert.

## Usage

dotnet PgsToSrt.dll [parameters]

| Parameter             | Description                                                                                                                                      |
| --------------------- |--------------------------------------------------------------------------------------------------------------------------------------------------|
| `--input`             | Input filename, can be an mkv file or pgs subtitle extracted to a .sup file with mkvextract.                                                     |
| `--output`            | Output SubRip (`.srt`) filename. Auto generated from input filename if not set.                                                                  |
| `--track`             | Track number of the subtitle to process in an `.mkv` file (only required when input is a matroska file) <br/>This can be obtained with `mkvinfo` |
| `--tracklanguage`     | Convert all tracks of the specified language (only works with `.mkv` input)                                                               |
| `--tesseractlanguage` | Tesseract language to use if multiple languages are available in the tesseract data directory.                                                   |
| `--tesseractdata`     | Path of tesseract language data files, by default `tessdata` in the executable directory.                                                        |
| `--tesseractversion`  | libtesseract version, support 4 and 5 (default: 4) (ignored on Windows platform)                                                                 |
| `--libleptname`       | leptonica library name, usually lept or leptonica, 'lib' prefix is automatically added (default: lept) (ignored on Windows platform)             |
| `--libleptversion`    | leptonica library version (default: 5) (ignored on Windows platform)                                                                             |
| `-b, --blacklist`     | Character blacklist to improve OCR accuracy (e.g., `"\|\/\`_~<>"` to exclude commonly misrecognized characters)                                 |
| `--short-threshold`   | Duration threshold in milliseconds - subtitles shorter than this will be extended (default: 300ms). Set to 0 to disable.                        |
| `--extend-to`         | Duration to extend short subtitles to in milliseconds (default: 1200ms)                                                                          |

## Short Subtitle Extension

Very short subtitles (often under 300ms) can fail to display properly in some video players. PgsToSrt can automatically extend these subtitles to a minimum duration:

- `--short-threshold 250`: Extend subtitles shorter than 250ms
- `--extend-to 1500`: Extend those short subtitles to 1500ms duration
- `--short-threshold 0`: Disable short subtitle extension

The extension respects subtitle timing - it won't extend a subtitle if it would overlap with the next one.

## Example (Command Line)

``` sh
# Basic conversion
dotnet PgsToSrt.dll --input video1.fr.sup --output video1.fr.srt --tesseractlanguage fra

# Convert from MKV with track selection
dotnet PgsToSrt.dll --input video1.mkv --output video1.srt --track 4

# With character blacklist to improve OCR
dotnet PgsToSrt.dll --input video1.sup --blacklist "|\/`_~<>" --tesseractlanguage eng

# Extend short subtitles (under 250ms) to 1500ms duration
dotnet PgsToSrt.dll --input video1.sup --short-threshold 250 --extend-to 1500

# Disable short subtitle extension
dotnet PgsToSrt.dll --input video1.sup --short-threshold 0
```

## Example (Docker)

Examine `entrypoint.sh` for a full list of all available arguments.

``` sh
docker run -it --rm \
    -v /data:/data \
    -e INPUT=/data/myImageSubtitle.sup \
    -e OUTPUT=/data/myTextSubtitle.srt \
    -e LANGUAGE=eng \
    -e CHARACTER_BLACKLIST="|\/\`_~<>" \
    -e SHORT_THRESHOLD=300 \
    -e EXTEND_TO=1200 \
    tentacule/pgstosrt
```

### Docker Environment Variables

| Variable              | Description                                                    | Default |
|-----------------------|----------------------------------------------------------------|---------|
| `INPUT`               | Input file path                                                | Required |
| `OUTPUT`              | Output SRT file path                                           | Required |
| `TRACK`               | Track number for MKV files                                     | -       |
| `TRACK_LANGUAGE`      | Track language filter                                          | -       |
| `LANGUAGE`            | Tesseract OCR language                                         | eng     |
| `TESSDATA`            | Path to tessdata folder                                        | /tessdata |
| `CHARACTER_BLACKLIST` | Character blacklist for OCR                                    | -       |
| `SHORT_THRESHOLD`     | Threshold for short subtitle extension (ms)                   | 300     |
| `EXTEND_TO`           | Target duration for short subtitles (ms)                      | 1200    |

Hint: The default arguments coming from `Dockerfile` are `INPUT=/input.sup` and `OUTPUT=/output.srt`, so you can easily:

``` sh
touch output-file.srt  # This needs to be a file, otherwise Docker will just assume it's a directory mount and it will fail.
docker run --it -rm \
    -v source-file.sup:/input.sup \
    -v output-file.srt:/output.srt \
    -e LANGUAGE=eng \
    -e SHORT_THRESHOLD=250 \
    -e EXTEND_TO=1500 \
    tentacule/pgstosrt
```

## Dependencies

- Linux: libtesseract5 (`sudo apt install libtesseract5` or equivalent for your distribution)

## Build

To build PgsToSrt.dll execute the following commands:

``` sh
dotnet restore PgsToSrt/PgsToSrt.csproj
dotnet publish PgsToSrt/PgsToSrt.csproj -c Release -o out --framework net8.0
# The file produced is out/PgsToSrt.dll
```

For a standalone Linux executable:

``` sh
./publish.sh 1.0.1
# Creates PgsToSrt-1.0.1-linux-x64.tar.gz
```

To build a Docker image for all languages:

``` sh
make build-all
```

To build a docker image for a single language:

``` sh
make build-single LANGUAGE=eng  # or any other Tesseract-available language code
```

## Release Process

Create a new release by pushing a git tag:

``` sh
git tag v1.0.1
git push origin v1.0.1
```

This automatically builds and uploads Linux binaries to the GitHub release.

## Built With

- LibSE from [Subtitle Edit](https://www.nikse.dk/SubtitleEdit/)
- [Tesseract .net wrapper](https://github.com/charlesw/tesseract/)
- [CommandLineParser](https://github.com/commandlineparser/commandline)
- [SixLabors ImageSharp](https://github.com/SixLabors/ImageSharp)