using System.Collections.Generic;
using System.Linq;
using Marvin.Core;

namespace Marvin;

public static class RaceVoiceChecks
{
    /// Deterministic conversational regressions: rank crossings, hysteresis, no ambient spam,
    /// non-repeating bags and audible range. Separate from subjective sound audition.
    public static Dictionary<string, bool> checkRaceVoiceDirector()
    {
        RaceVoiceDirector.Observation[] observations(double delta, double speed = 4, bool contact = false, bool far = false) =>
            Enumerable.Range(0, 4).Select(i =>
                new RaceVoiceDirector.Observation(position: new Double2(i == 1 && !far ? 2 : (double)i * 30, 0), speed: speed, contact: contact,
                    progress: 20 + (i == 1 ? delta : (double)i * 2))).ToArray();
        var steady = new RaceVoiceDirector(seed: 1); var steadyEvents = new List<RaceVoiceDirector.Event>();
        for (int k = 0; k < 36000; k++)
        {
            if (steady.advance(observations: observations(-0.1, far: true), dt: 1.0 / 60) is RaceVoiceDirector.Event e) steadyEvents.Add(e);
        }
        var checks = new Dictionary<string, bool> { ["tenMinutesNoPeriodicChatter"] = steadyEvents.Count == 1 && steadyEvents[0].robot == 0 };
        var director = new RaceVoiceDirector(seed: 2); var events = new List<RaceVoiceDirector.Event>();
        for (int pass = 0; pass < 14; pass++)
        {
            var delta = pass % 2 == 0 ? -0.1 : 0.1;
            for (int k = 0; k < 1500; k++)
            {
                if (director.advance(observations: observations(delta), dt: 1.0 / 60) is RaceVoiceDirector.Event e) events.Add(e);
            }
        }
        var overtakes = events.Where(e => e.mood == "overtake").ToList(); var passed = events.Where(e => e.mood == "passed").ToList();
        checks["bothOvertakeDirections"] = overtakes.Count == 7 && passed.Count == 6 && overtakes.Concat(passed).All(e => e.robot == 1);
        checks["sixVariantsBeforeRepeat"] = overtakes.Count >= 7 && passed.Count >= 6 && overtakes.Take(6).Select(e => e.variant).ToHashSet().Count == 6
            && passed.Take(6).Select(e => e.variant).ToHashSet().Count == 6 && overtakes[5].variant != overtakes[6].variant;
        var jitter = new RaceVoiceDirector(seed: 3); int falsePasses = 0;
        for (int frame = 0; frame < 600; frame++)
        {
            var delta = frame == 0 ? -0.1 : (frame % 2 == 0 ? -0.02 : 0.02);
            if (jitter.advance(observations: observations(delta), dt: 1.0 / 60) is RaceVoiceDirector.Event e && (e.mood == "overtake" || e.mood == "passed")) falsePasses += 1;
        }
        checks["sideBySideHysteresis"] = falsePasses == 0;
        var far = new RaceVoiceDirector(seed: 4); int farSpeech = 0;
        for (int frame = 0; frame < 1200; frame++)
        {
            if (far.advance(observations: observations(frame < 600 ? -0.1 : 0.1, far: true), dt: 1.0 / 60) is RaceVoiceDirector.Event e && e.robot != 0) farSpeech += 1;
        }
        checks["distantRobotsSilent"] = farSpeech == 0;
        foreach (var mood in new[] { "acknowledge", "startle" })
        {
            var moodDirector = new RaceVoiceDirector(seed: 5); var moodEvents = new List<RaceVoiceDirector.Event>();
            for (int frame = 0; frame < 180; frame++)
            {
                if (moodDirector.advance(observations: observations(-0.1, speed: frame < 24 ? 0 : 7, contact: mood == "startle" && frame >= 24, far: true), dt: 1.0 / 60) is RaceVoiceDirector.Event e)
                    moodEvents.Add(e);
            }
            checks[mood] = moodEvents.Count == 1 && moodEvents[0].mood == mood;
        }
        var busy = new RaceVoiceDirector(seed: 9); int busyCount = 0;
        for (int frame = 0; frame < 36000; frame++)
        {
            var speed = frame % 240 < 60 ? 1.0 : 7.0;
            if (busy.advance(observations: observations(frame % 600 < 300 ? -0.1 : 0.1, speed: speed, contact: frame % 30 == 0), dt: 1.0 / 60) != null) busyCount += 1;
        }
        checks["busyRaceSpeechBudget"] = busyCount <= 76;
        return checks;
    }
}
