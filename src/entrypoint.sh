#!/bin/bash

args=()

if [[ -n "${INPUT}" ]]; then
  args+=('--input' "${INPUT}")
fi
if [[ -n "${OUTPUT}" ]]; then
  args+=('--output' "${OUTPUT}")
fi
if [[ -n "${TRACK}" ]]; then
  args+=('--track' "${TRACK}")
fi
if [[ -n "${TRACK_LANGUAGE}" ]]; then
  args+=('--tracklanguage' "${TRACK_LANGUAGE}")
fi
if [[ -n "${LANGUAGE}" ]]; then
  args+=('--tesseractlanguage' "${LANGUAGE}")
fi
if [[ -n "${TESSDATA}" ]]; then
  args+=('--tesseractdata' "${TESSDATA}")
else
  args+=('--tesseractdata' '/tessdata')
fi
if [[ -n "${CHARACTER_BLACKLIST}" ]]; then
  args+=('--blacklist' "${CHARACTER_BLACKLIST}")
fi
if [[ -n "${SHORT_THRESHOLD}" ]]; then
  args+=('--short-threshold' "${SHORT_THRESHOLD}")
fi
if [[ -n "${EXTEND_TO}" ]]; then
  args+=('--extend-to' "${EXTEND_TO}")
fi

args+=('--tesseractversion' '5')
  
echo "dotnet /app/PgsToSrt.dll ${args[*]}"
dotnet /app/PgsToSrt.dll "${args[@]}"