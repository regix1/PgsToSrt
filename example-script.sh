/**
 * @description Convert PGS/SUP subtitle files to SRT format using OCR (PgsToSrt.dll) and optionally mux into MKV.
 * @param {string} TrackLanguageFilter Language code(s) to filter PGS tracks from MKV (e.g. "eng", "fre,ger"). Default "eng". Leave empty to consider all PGS tracks.
 * @param {string} OcrLanguage Language for PgsToSrt.dll OCR processing (e.g. "eng", "deu", default: "eng"). This should be a single 3-letter language code supported by Tesseract.
 * @param {string} TesseractPath Path to tesseract's tessdata folder. Defaults to the path used by the provided install script.
 * @param {bool} MuxToMkv If true and input is MKV, muxes the created SRTs back into a new MKV file. If false, SRTs are saved externally. (default: true)
 * @param {bool} FilterOutEngForeign If true, specifically filters out English PGS tracks named with "(Foreign)" (case-insensitive). (default: false)
 * @param {bool} SkipIfNoSubtitles Skip processing if no applicable PGS tracks found (default: true)
 * @param {string} FilePermissions Permissions for external SRT files (default: "0666").
 * @param {int} FileWaitMs Time to wait for file operations in milliseconds (default: 5000)
 * @param {string} Blacklist Character blacklist to improve OCR (e.g., "|\/`_~<>", default: empty).
 * @param {int} ShortThreshold Duration threshold in milliseconds - subtitles shorter than this will be extended (default: 300). Set to 0 to disable.
 * @param {int} ExtendTo Duration to extend short subtitles to in milliseconds (default: 1200).
 * @output 1 Subtitles processed: SRTs created and either muxed or saved externally, or skipped successfully.
 * @output -1 Processing failed, or no SRTs were successfully generated from eligible tracks that were attempted.
 */
function Script(TrackLanguageFilter, OcrLanguage, TesseractPath, MuxToMkv, FilterOutEngForeign, SkipIfNoSubtitles, FilePermissions, FileWaitMs, Blacklist, ShortThreshold, ExtendTo) {
    
    // --- Constants for PgsToSrt detection ---
    const PGSTOSRT_INSTALL_DIR = "/opt/pgstosrt";
    const PGSTOSRT_EXECUTABLE = PGSTOSRT_INSTALL_DIR + "/PgsToSrt";
    const PGSTOSRT_DLL_PATH = PGSTOSRT_INSTALL_DIR + "/PgsToSrt.dll";
    const DEFAULT_TESSDATA_PATH = PGSTOSRT_INSTALL_DIR + "/tessdata";
    const MKVMERGE_EXECUTABLE = "/usr/bin/mkvmerge";
    const MKVEXTRACT_EXECUTABLE = "/usr/bin/mkvextract";

    // Detect which PgsToSrt version is available
    let pgsToSrtMode = "none";
    if (System.IO.File.Exists(PGSTOSRT_EXECUTABLE)) {
        pgsToSrtMode = "executable";
        Logger.ILog(`Found PgsToSrt standalone executable: ${PGSTOSRT_EXECUTABLE}`);
    } else if (System.IO.File.Exists(PGSTOSRT_DLL_PATH)) {
        pgsToSrtMode = "dll";
        Logger.ILog(`Found PgsToSrt .dll: ${PGSTOSRT_DLL_PATH}`);
    } else {
        Logger.ELog("No PgsToSrt installation found. Checked executable and dll.");
        return -1;
    }

    function safeString(param, defaultValue = "") {
        if (param === undefined || param === null) {
            return defaultValue;
        }
        return String(param);
    }

    function safeInt(param, defaultValue = 0) {
        if (param === undefined || param === null) {
            return defaultValue;
        }
        const parsed = parseInt(param, 10);
        return isNaN(parsed) ? defaultValue : parsed;
    }

    // Parameter Initialization
    if (TrackLanguageFilter === undefined || TrackLanguageFilter === null) {
        TrackLanguageFilter = "eng"; 
    } else {
        TrackLanguageFilter = String(TrackLanguageFilter); 
    }

    OcrLanguage = safeString(OcrLanguage, "eng"); 
    TesseractPath = safeString(TesseractPath, DEFAULT_TESSDATA_PATH);
    MuxToMkv = MuxToMkv === undefined ? true : MuxToMkv !== false;
    FilterOutEngForeign = FilterOutEngForeign === undefined ? false : FilterOutEngForeign === true;
    SkipIfNoSubtitles = SkipIfNoSubtitles !== false;
    FilePermissions = safeString(FilePermissions, "0666");
    FileWaitMs = FileWaitMs === undefined ? 5000 : parseInt(FileWaitMs, 10);
    Blacklist = safeString(Blacklist, "");
    ShortThreshold = safeInt(ShortThreshold, 300);
    ExtendTo = safeInt(ExtendTo, 1200);

    let workingFile = Flow.WorkingFile;
    let originalFileNameForOutput = Flow.OriginalFile;
    if (!originalFileNameForOutput) {
        Logger.WLog("Flow.OriginalFile is undefined. Falling back to Flow.WorkingFile for output path base.");
        originalFileNameForOutput = Flow.WorkingFile;
    }
    let fileExt = workingFile.substring(workingFile.lastIndexOf('.') + 1).toLowerCase();

    Logger.ILog("=== PGS/SUP TO SRT CONVERTER (OCR) SCRIPT (UPDATED FOR NEW PGSTOSRT) ===");
    Logger.ILog(`Working File (temp): ${workingFile}`);
    Logger.ILog(`Original File (for output ref): ${originalFileNameForOutput}`);
    Logger.ILog(`OCR Language for PgsToSrt: ${OcrLanguage}`);
    Logger.ILog(`Tesseract Data Path for PgsToSrt: ${TesseractPath}`);
    Logger.ILog(`PgsToSrt Mode: ${pgsToSrtMode}`);
    if (Blacklist && Blacklist.trim() !== "") {
        Logger.ILog(`Character blacklist for OCR: '${Blacklist}'`);
    }
    if (ShortThreshold > 0 && ExtendTo > 0) {
        Logger.ILog(`Short subtitle extension: subtitles < ${ShortThreshold}ms will be extended to ${ExtendTo}ms`);
    } else {
        Logger.ILog("Short subtitle extension: disabled");
    }
    if (MuxToMkv && fileExt === "mkv") Logger.ILog("MUX TO MKV ENABLED for MKV input.");
    if (FilterOutEngForeign) Logger.ILog("FILTER ENG FOREIGN ENABLED.");

    const mkvTrackLangFilterArray = TrackLanguageFilter.trim() ? 
        TrackLanguageFilter.toLowerCase().split(',').map(l => l.trim()).filter(l => l) :
        [];

    if (mkvTrackLangFilterArray.length > 0) {
        Logger.ILog(`Filtering MKV PGS tracks for languages: ${mkvTrackLangFilterArray.join(', ')} (and 'und'etermined)`);
    } else {
        Logger.ILog("No MKV Track Language Filter specified: all PGS tracks will be considered (subject to other filters).");
    }

    if (fileExt === "mkv") {
        return processMkvFile();
    } else if (fileExt === "sup") {
        if (MuxToMkv) Logger.ILog("Note: MuxToMkv is true, but input is a standalone SUP. Output will be an external SRT.");
        return processSupFile(workingFile, originalFileNameForOutput);
    } else {
        Logger.WLog(`Unsupported file type: '${fileExt}'. Supported: MKV, SUP.`);
        return SkipIfNoSubtitles ? 1 : -1;
    }

    function processMkvFile() {
        Logger.ILog("MKV file detected. Identifying PGS subtitle tracks...");
        let mkvMergeIdResult = Flow.Execute({
            command: MKVMERGE_EXECUTABLE,
            argumentList: ["-i", "-F", "json", workingFile]
        });

        if (mkvMergeIdResult.exitCode !== 0) {
            Logger.ELog(`Failed to get track info (mkvmerge). Exit: ${mkvMergeIdResult.exitCode}. Stderr: ${mkvMergeIdResult.standardError || 'N/A'}`);
            return -1;
        }

        let tracksInfoJson;
        try {
            tracksInfoJson = JSON.parse(mkvMergeIdResult.output);
        } catch (e) {
            Logger.ELog(`Failed to parse mkvmerge JSON: ${e.message}. Output: ${mkvMergeIdResult.output}`);
            return -1;
        }

        let pgsTracksFound = [];
        if (tracksInfoJson && tracksInfoJson.tracks) {
            tracksInfoJson.tracks.forEach(track => {
                if (track.type === "subtitles" && (track.codec === "HDMV PGS" || track.codec_id === "S_HDMV/PGS")) {
                    let trackId = track.id;
                    let trackName = track.properties && track.properties.track_name ? track.properties.track_name : 'Untitled PGS Track';
                    let lang = track.properties && track.properties.language ? track.properties.language.toLowerCase() : "und";
                    let langIETF = track.properties ? (track.properties.language_ietf || track.properties.language || 'N/A') : 'N/A';
                    pgsTracksFound.push({ id: trackId.toString(), language: lang, originalLanguageTag: langIETF, trackName: trackName });
                }
            });
        }

        if (pgsTracksFound.length === 0) {
            Logger.WLog("No PGS subtitle tracks found in the MKV.");
            return SkipIfNoSubtitles ? 1 : -1;
        }

        Logger.ILog(`Found ${pgsTracksFound.length} raw PGS tracks. Applying filters...`);
        let currentFilteredTracks = pgsTracksFound;

        if (mkvTrackLangFilterArray.length > 0) {
            const countBefore = currentFilteredTracks.length;
            currentFilteredTracks = currentFilteredTracks.filter(track => {
                let trackLangSimple = track.language.substring(0, 2);
                return mkvTrackLangFilterArray.includes(track.language) || mkvTrackLangFilterArray.includes(trackLangSimple) || track.language === "und";
            });
            Logger.ILog(`Tracks after MKV Track Language filter ('${TrackLanguageFilter}'): ${currentFilteredTracks.length} (was ${countBefore}).`);
        }
        
        if (FilterOutEngForeign) {
            const countBeforeEngForeign = currentFilteredTracks.length;
            currentFilteredTracks = currentFilteredTracks.filter(track => {
                const isEnglish = track.language === "eng" || track.language.substring(0,2) === "en";
                const isMarkedForeign = track.trackName && /\(foreign\)/i.test(track.trackName.toLowerCase());
                if (isEnglish && isMarkedForeign) {
                    Logger.ILog(`Excluding PGS Track ID ${track.id} (Name: "${track.trackName}", Lang: ${track.language}) due to "FilterOutEngForeign".`);
                    return false;
                }
                return true;
            });
            Logger.ILog(`Tracks after 'FilterOutEngForeign' filter: ${currentFilteredTracks.length} (was ${countBeforeEngForeign}).`);
        }
        
        let finalFilteredTracks = currentFilteredTracks;
        if (finalFilteredTracks.length === 0) {
            Logger.WLog("No PGS tracks remain after all filters.");
            return SkipIfNoSubtitles ? 1 : -1;
        }

        Logger.ILog(`Attempting to process ${finalFilteredTracks.length} PGS subtitle tracks.`);
        let baseNameForTempFiles = workingFile.substring(workingFile.lastIndexOf(Flow.IsWindows ? '\\' : '/') + 1, workingFile.lastIndexOf('.'));
        let srtFilesDataForProcessing = [];

        for (let i = 0; i < finalFilteredTracks.length; i++) {
            let track = finalFilteredTracks[i];
            let supFilePath = `${Flow.TempPath}/${baseNameForTempFiles}.${track.id}_${i}.sup`;
            Logger.ILog(`Processing PGS Track ID ${track.id} (Name: "${track.trackName}", MKV Lang: ${track.language}, Index: ${i}). Extracting to: ${supFilePath}`);

            let extractResult = Flow.Execute({
                command: MKVEXTRACT_EXECUTABLE,
                argumentList: ["tracks", workingFile, `${track.id}:${supFilePath}`]
            });

            if (extractResult.exitCode !== 0) {
                Logger.ELog(`mkvextract failed for PGS track ${track.id}. Exit: ${extractResult.exitCode}. Stderr: ${extractResult.standardError || 'N/A'}`);
                continue;
            }
            System.Threading.Thread.Sleep(FileWaitMs);
            if (!System.IO.File.Exists(supFilePath)) {
                Logger.ELog(`Extracted SUP file ${supFilePath} not found. Skipping track.`);
                continue;
            }
            Logger.ILog(`Extracted ${baseNameForTempFiles}.${track.id}_${i}.sup`);

            let langForOcrTool = track.language;
            if (langForOcrTool === "und" || langForOcrTool.length !== 3) {
                langForOcrTool = OcrLanguage; 
            }

            let srtFilePathInTemp = convertSupToSrt(supFilePath, `${baseNameForTempFiles}.${track.id}_${i}`, langForOcrTool);

            if (srtFilePathInTemp && System.IO.File.Exists(srtFilePathInTemp)) {
                Logger.ILog(`Temporary SRT created for track ${track.id}: ${srtFilePathInTemp}`);
                srtFilesDataForProcessing.push({ srtPath: srtFilePathInTemp, originalTrackData: track, processingIndex: i });
            } else {
                Logger.ELog(`Failed to convert SUP to SRT for track ${track.id} from ${supFilePath}.`);
            }
            try { if (System.IO.File.Exists(supFilePath)) System.IO.File.Delete(supFilePath); } 
            catch (eDelSup) { Logger.WLog(`Could not delete temp SUP file ${supFilePath}: ${eDelSup.message}`); }
        } 

        if (srtFilesDataForProcessing.length === 0) {
            Logger.ILog("No SRT files were generated from PGS tracks for further processing.");
            return finalFilteredTracks.length > 0 ? -1 : 1; 
        }

        let finalOutputWorkingFile = workingFile; 
        let overallSuccess = false;

        if (MuxToMkv) {
            Logger.ILog(`MuxToMkv is true. Attempting to mux ${srtFilesDataForProcessing.length} SRT(s) into MKV.`);
            let muxedOutputTempMkv = `${Flow.TempPath}/${baseNameForTempFiles}_muxed_pgs.mkv`;
            let mkvmergeArgs = ['-o', muxedOutputTempMkv, finalOutputWorkingFile]; 

            for (let srtData of srtFilesDataForProcessing) {
                let langForMux = srtData.originalTrackData.language || OcrLanguage; 
                if (langForMux === "und" || langForMux.trim() === "") langForMux = OcrLanguage;
                if (langForMux.length !== 3) langForMux = OcrLanguage; 

                mkvmergeArgs.push('--language', `0:${langForMux.trim().substring(0,3)}`);
                let srtTrackName = srtData.originalTrackData.trackName ? `${srtData.originalTrackData.trackName} (SRT from PGS)` : `PGS Track ${srtData.originalTrackData.id} (SRT)`;
                mkvmergeArgs.push('--track-name', `0:${srtTrackName}`);
                mkvmergeArgs.push('--default-track', '0:no'); 
                mkvmergeArgs.push('--forced-track', '0:no');   
                mkvmergeArgs.push(srtData.srtPath);
            }
            
            Logger.ILog(`Executing mkvmerge for muxing. Output: ${muxedOutputTempMkv}`);
            let muxResult = Flow.Execute({ command: MKVMERGE_EXECUTABLE, argumentList: mkvmergeArgs });

            if (muxResult.exitCode === 0 && System.IO.File.Exists(muxedOutputTempMkv)) {
                Logger.ILog("Muxing successful. Updating working file.");
                Flow.SetWorkingFile(muxedOutputTempMkv); 
                Logger.ILog(`Working file updated to muxed version: ${muxedOutputTempMkv}`);
                overallSuccess = true;
                if (workingFile !== muxedOutputTempMkv && System.IO.File.Exists(workingFile)) {
                     try { System.IO.File.Delete(workingFile); Logger.ILog(`Deleted original temp MKV: ${workingFile}`);}
                     catch(eDelOrigTemp) { Logger.WLog(`Could not delete original temp MKV ${workingFile}: ${eDelOrigTemp.message}`);}
                }
            } else {
                Logger.ELog(`Mkvmerge for muxing failed. Exit: ${muxResult.exitCode}. Stderr: ${muxResult.standardError || 'N/A'}`);
                Logger.WLog("Falling back to creating external SRT files.");
                if (System.IO.File.Exists(muxedOutputTempMkv)) try {System.IO.File.Delete(muxedOutputTempMkv);}catch(e){} 
                overallSuccess = copySrtsExternallyAndCleanup(srtFilesDataForProcessing, originalFileNameForOutput) > 0;
                Flow.SetWorkingFile(workingFile);
            }
        } else { 
            Logger.ILog("MuxToMkv is false. Creating external SRT files.");
            overallSuccess = copySrtsExternallyAndCleanup(srtFilesDataForProcessing, originalFileNameForOutput) > 0;
            Flow.SetWorkingFile(workingFile);
        }
        
        srtFilesDataForProcessing.forEach(sfd => {
             try { if(System.IO.File.Exists(sfd.srtPath)) System.IO.File.Delete(sfd.srtPath); } 
             catch (e) { Logger.WLog(`Final cleanup: Could not delete temp SRT: ${sfd.srtPath} - ${e.message}`); }
        });

        return overallSuccess ? 1 : -1;
    }
    
    function copySrtsExternallyAndCleanup(srtFilesDataList, refOriginalFile) {
        let createdCount = 0;
        let origFileDir = refOriginalFile.substring(0, refOriginalFile.lastIndexOf(Flow.IsWindows ? '\\' : '/'));
        let origFileBaseName = refOriginalFile.substring(refOriginalFile.lastIndexOf(Flow.IsWindows ? '\\' : '/') + 1, refOriginalFile.lastIndexOf('.'));

        for (let srtData of srtFilesDataList) {
            let langSuffix = (srtData.originalTrackData.language && srtData.originalTrackData.language !== "und" && srtData.originalTrackData.language !== "unknown") ? `.${srtData.originalTrackData.language}` : '';
            let destSrt = `${origFileDir}/${origFileBaseName}${langSuffix}.${srtData.originalTrackData.id}_${srtData.processingIndex}.pgs.srt`; 

            try {
                System.IO.File.Copy(srtData.srtPath, destSrt, true);
                Logger.ILog(`Copied external SRT to: ${destSrt}`);
                if (!Flow.IsWindows && FilePermissions) {
                    Flow.Execute({ command: "chmod", argumentList: [FilePermissions, destSrt] });
                }
                createdCount++;
            } catch (eCopyExt) {
                Logger.ELog(`Failed to copy external SRT from ${srtData.srtPath} to ${destSrt}: ${eCopyExt.message}`);
            }
        }
        return createdCount;
    }

    function processSupFile(supInputPath, originalSupFileNameRef) {
        Logger.ILog(`Standalone SUP file detected: ${supInputPath}`);
        let baseNameForSrt = originalSupFileNameRef.substring(originalSupFileNameRef.lastIndexOf(Flow.IsWindows ? '\\' : '/') + 1, originalSupFileNameRef.lastIndexOf('.'));
        
        let ocrLangForSup = OcrLanguage || "eng"; 

        let srtFilePathInTemp = convertSupToSrt(supInputPath, baseNameForSrt, ocrLangForSup);

        if (!srtFilePathInTemp || !System.IO.File.Exists(srtFilePathInTemp)) {
            Logger.ELog(`Failed to convert standalone SUP file ${supInputPath} to SRT.`);
            return -1;
        }

        Logger.ILog(`SRT file created at: ${srtFilePathInTemp}. Copying to original location...`);
        let origFileDir = originalSupFileNameRef.substring(0, originalSupFileNameRef.lastIndexOf(Flow.IsWindows ? '\\' : '/'));
        let destSrt = `${origFileDir}/${baseNameForSrt}.sup.srt`; 

        try {
            System.IO.File.Copy(srtFilePathInTemp, destSrt, true);
            Logger.ILog(`Copied standalone SRT to: ${destSrt}`);
            if (!Flow.IsWindows && FilePermissions) {
                Flow.Execute({ command: "chmod", argumentList: [FilePermissions, destSrt] });
            }
            Flow.SetWorkingFile(destSrt); 
            try { if(System.IO.File.Exists(srtFilePathInTemp)) System.IO.File.Delete(srtFilePathInTemp); } catch(e){}
            return 1;
        } catch (eCopy) {
            Logger.ELog(`Failed to copy standalone SRT from ${srtFilePathInTemp} to ${destSrt}: ${eCopy.message}`);
            try { if(System.IO.File.Exists(srtFilePathInTemp)) System.IO.File.Delete(srtFilePathInTemp); } catch(e){}
            return -1;
        }
    }
    
    function convertSupToSrt(supFilePath, baseNameForSrt, ocrLanguageForTool) {
        let srtFilePath = `${Flow.TempPath}/${baseNameForSrt}.srt`;
        let effectiveOcrLang = (ocrLanguageForTool && ocrLanguageForTool.trim().length === 3) ? ocrLanguageForTool.trim() : "eng";
        if (ocrLanguageForTool && ocrLanguageForTool.trim().length !== 3 && ocrLanguageForTool.trim() !== "") {
            Logger.WLog(`Provided OCR language '${ocrLanguageForTool}' for PgsToSrt is not a 3-letter code. Defaulting to 'eng'.`);
            effectiveOcrLang = "eng";
        }

        Logger.ILog(`Converting SUP "${supFilePath}" to SRT "${srtFilePath}" using OCR language "${effectiveOcrLang}" for PgsToSrt...`);

        // Build common argument list for blacklist and extension parameters
        let commonArgs = [];
        if (Blacklist && Blacklist.trim() !== "") {
            commonArgs.push("--blacklist", Blacklist.trim());
            Logger.ILog(`Adding blacklist parameter: '${Blacklist.trim()}'`);
        }
        if (ShortThreshold > 0 && ExtendTo > 0) {
            commonArgs.push("--short-threshold", ShortThreshold.toString());
            commonArgs.push("--extend-to", ExtendTo.toString());
            Logger.ILog(`Adding short subtitle extension: threshold=${ShortThreshold}ms, extend-to=${ExtendTo}ms`);
        }

        // Try different PgsToSrt calling methods based on what's available
        if (pgsToSrtMode === "executable") {
            // Method 1: Standalone executable
            let executableArgs = [
                "--input", supFilePath,
                "--output", srtFilePath,
                "--tesseractlanguage", effectiveOcrLang,
                "--tesseractdata", TesseractPath,
                "--tesseractversion", "4",
                "--libleptname", "lept",
                "--libleptversion", "5"
            ].concat(commonArgs);

            Logger.ILog(`Attempting PgsToSrt standalone executable: ${PGSTOSRT_EXECUTABLE} ${executableArgs.join(' ')}`);
            let executableResult = Flow.Execute({
                command: PGSTOSRT_EXECUTABLE,
                argumentList: executableArgs,
                workingDirectory: PGSTOSRT_INSTALL_DIR
            });

            Logger.ILog(`PgsToSrt executable exit code: ${executableResult.exitCode}`);

            if (executableResult.exitCode === 0 && System.IO.File.Exists(srtFilePath) && new System.IO.FileInfo(srtFilePath).Length > 0) {
                Logger.ILog(`PgsToSrt conversion successful (standalone executable). SRT: ${srtFilePath}`);
                return srtFilePath;
            } else {
                Logger.WLog("Standalone executable failed or produced empty SRT. Trying other methods...");
            }
        }

        if (pgsToSrtMode === "dll" || pgsToSrtMode === "executable") {
            // Method 2: dotnet dll (fallback or primary for dll mode)
            let dllArgs = [
                "PgsToSrt.dll",
                "--input", supFilePath,
                "--output", srtFilePath,
                "--tesseractlanguage", effectiveOcrLang,
                "--tesseractdata", TesseractPath,
                "--tesseractversion", "4",
                "--libleptname", "lept",
                "--libleptversion", "5"
            ].concat(commonArgs);

            Logger.ILog(`Attempting PgsToSrt.dll: dotnet ${dllArgs.join(' ')}`);
            let dotnetResult = Flow.Execute({
                command: "dotnet",
                argumentList: dllArgs,
                workingDirectory: PGSTOSRT_INSTALL_DIR
            });

            Logger.ILog(`PgsToSrt.dll (dotnet) exit code: ${dotnetResult.exitCode}`);

            if (dotnetResult.exitCode === 0 && System.IO.File.Exists(srtFilePath) && new System.IO.FileInfo(srtFilePath).Length > 0) {
                Logger.ILog(`PgsToSrt.dll conversion successful. SRT: ${srtFilePath}`);
                return srtFilePath;
            } else {
                Logger.WLog("Direct PgsToSrt.dll call failed or produced empty SRT.");
            }
        }

        Logger.ELog(`All PgsToSrt methods failed to convert ${supFilePath} or produced an empty SRT.`);
        return null; 
    }
}
