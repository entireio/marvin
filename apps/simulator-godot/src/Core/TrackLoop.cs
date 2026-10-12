using System;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Closed belt centerline in the robot's Y/Z plane. Positive travel moves the
/// bottom run backward (-Z), canceling forward chassis motion at ground contact.
public static class TrackLoop
{
    public const double radius = 0.11935, centerY = 0.12485;
    public const double rear = -0.24152, front = 0.18894;
    public const double straight = front - rear;
    public const double circumference = 2 * straight + 2 * Math.PI * radius;
    public static (double y, double z, double angle) sample(double travel)
    {
        var s = travel % circumference;
        if (s < 0) s += circumference;
        if (s < straight) return (centerY - radius, front - s, Math.PI);
        s -= straight;
        if (s < Math.PI * radius)
        {
            var a = s / radius;
            return (centerY - radius * cos(a), rear - radius * sin(a), Math.PI + a);
        }
        s -= Math.PI * radius;
        if (s < straight) return (centerY + radius, rear + s, 0);
        var b = (s - straight) / radius;
        return (centerY + radius * cos(b), front + radius * sin(b), b);
    }
}
