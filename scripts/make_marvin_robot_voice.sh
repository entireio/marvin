#!/usr/bin/env bash
set -euo pipefail

input=${1:?Usage: make_marvin_robot_voice.sh INPUT OUTPUT_WAV OUTPUT_M4A}
output_wav=${2:?Usage: make_marvin_robot_voice.sh INPUT OUTPUT_WAV OUTPUT_M4A}
output_m4a=${3:?Usage: make_marvin_robot_voice.sh INPUT OUTPUT_WAV OUTPUT_M4A}

filter="[0:a]aformat=sample_fmts=fltp:sample_rates=44100,highpass=f=85,lowpass=f=9500,acompressor=threshold=0.125:ratio=2.2:attack=8:release=100:makeup=1.35,asplit=3[clean][metal][ring];[clean]equalizer=f=260:t=q:w=1.1:g=-1.5,equalizer=f=2850:t=q:w=1.0:g=1.8,volume=0.92[cleaned];[metal]flanger=delay=0.8:depth=1.6:regen=14:width=32:speed=0.22:shape=triangular:phase=25:interp=quadratic,acrusher=bits=12:mix=0.12:mode=lin:aa=0.8,highpass=f=150,lowpass=f=6200,volume=0.22[metallic];[1:a]volume=0.7[carrier];[ring][carrier]amultiply,highpass=f=140,lowpass=f=4300,volume=0.085[robot];[cleaned][metallic][robot]amix=inputs=3:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.88:attack=5:release=60,loudnorm=I=-18:TP=-1.5:LRA=9[out]"

ffmpeg -y -hide_banner -loglevel warning \
  -i "$input" -f lavfi -i "sine=frequency=42:sample_rate=44100:duration=3600" \
  -filter_complex "$filter" -map '[out]' -ar 44100 -c:a pcm_s24le "$output_wav"

ffmpeg -y -hide_banner -loglevel warning \
  -i "$output_wav" -ar 44100 -c:a aac -b:a 192k "$output_m4a"
