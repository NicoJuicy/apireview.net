﻿using System;

namespace ApiReview.Shared
{
    public static class TimeFormatting
    {
        public static string Format(TimeSpan elapsedTime)
        {
            var totalYears = Math.Round(elapsedTime.TotalDays / 365, 0, MidpointRounding.AwayFromZero);
            var totalDays = Math.Round(elapsedTime.TotalDays, 0, MidpointRounding.AwayFromZero);
            var totalHours = Math.Round(elapsedTime.TotalHours, 0, MidpointRounding.AwayFromZero);
            var totalMinutes = Math.Round(elapsedTime.TotalMinutes, 0, MidpointRounding.AwayFromZero);

            if (totalYears > 1)
                return $"{totalYears:N0} years ago";
            else if (totalDays > 60)
                return $"{totalDays / 30:N0} months ago";
            else if (totalDays > 1)
                return $"{totalDays:N0} days ago";
            else if (totalHours > 1)
                return $"{totalHours:N0} hours ago";
            else if (totalMinutes > 1)
                return $"{totalMinutes:N0} minutes ago";
            else
                return $"just now";
        }

        public static string FormatAge(this DateTimeOffset dateTimeOffset)
        {
            var elapased = DateTimeOffset.Now.Subtract(dateTimeOffset);
            return Format(elapased);
        }
    }
}
