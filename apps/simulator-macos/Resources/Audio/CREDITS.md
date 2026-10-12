# Robot and crowd audio

Robot voice clips: transformed excerpts from “Generic Hero Effort Noises” /
“Valiant Genericus (Effort Noises)” by Tinsin, used under CC BY 3.0.
https://opengameart.org/content/generic-hero-effort-noises
https://creativecommons.org/licenses/by/3.0/
Changes: segmentation, time/pitch changes, vocoder/filter-bank processing,
original electronic carriers, envelopes, phrase arrangement and normalization.
No endorsement by the source performer is implied. These are original character
recreations, not film recordings or performances by the movie actors.

Robot motor and ground loops: adapted from “68 Workshop Sounds” by bart, CC0.
https://opengameart.org/content/68-workshop-sounds
https://creativecommons.org/publicdomain/zero/1.0/
Sources: drill long, machine, ratchet1, quiet scrape.
Changes: trimming, rate changes, filtering, level matching and loop crossfades.

crowd.wav: adapted from “07 - Soft cheering and chatter” in “Free Crowd Cheering
Sounds” by Gregor Quendel (2022), CC BY 4.0.
https://opengameart.org/content/free-crowd-cheering-sounds
https://www.gregorquendel.com/
https://creativecommons.org/licenses/by/4.0/
Changes: excerpt, mono conversion, filtering, gain normalization, loop crossfade.

sparse-crowd.wav: adapted from “Cheers” by Nocturnal_Vanguard / AuraVoice (2023), CC0.
https://opengameart.org/content/cheers-0
https://creativecommons.org/publicdomain/zero/1.0/
Changes: filtering, spaced excerpts, mono conversion, normalization.

finish-crowd.wav: adapted from “03 - Strong cheering - I” in Gregor Quendel's
“Free Crowd Cheering Sounds”, CC BY 4.0 (links above).
Changes: excerpt, filtering, mono conversion, normalization, loop crossfade.

2026-10 soundscape revision:
Market and cantina walla: “Crowded street at medieval market” by bolkmar (2018),
CC0, https://freesound.org/people/bolkmar/sounds/424790/
Source: publicly available HQ MP3 preview. Changes: overlapping perspectives,
filtering, level matching, loop crossfades and cantina percussion arrangement.

New low/high motors, contact, boost, impact, workshop and cantina percussion
also use bart's CC0 Workshop Sounds above (drill long, machine, ratchet1,
quiet scrape, low hammering, tool rummaging, clink, dull hammering,
wood on wood thuds, dull ping). Changes: layered/resampled recordings,
filtering, dynamic shaping, loop editing and original event arrangements.
Wind, sand wash, boost pressure and short race cues are original procedural DSP.
The original percussion arrangement is not the Star Wars cantina composition.
Existing vocal performances are shortened and remastered for restrained accents.
Generator: scripts/audio/soundscape.py, following characters.py.

Second October 2 revision (supersedes drivetrain source descriptions above):
All four low/high motors now use "motor noise.wav" by scivirus (2018), CC0,
https://freesound.org/people/scivirus/sounds/435730/
and "step48v3.wav" by escortmarius (2012), CC0,
https://freesound.org/people/escortmarius/sounds/140439/
Public HQ previews, trimmed, gain-stabilized, resampled, filtered, layered and
crossfaded. Source bytes and hashes: scripts/audio/sources.json.
Boost uses filtered original pressure noise and a low layer of scivirus's motor.
Rolling ground and sand textures are original DSP. No drill or ratchet recording
remains in any drivetrain, boost or ground-contact asset. Workshop ambience and
cantina percussion retain bart's workshop recordings. Voice assets are unchanged.
Rebuild this replacement set with soundscape.py SOURCE_DIRECTORY --mechanical-only.
