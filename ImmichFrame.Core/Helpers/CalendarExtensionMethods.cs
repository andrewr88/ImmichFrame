using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Models;

namespace ImmichFrame.Core.Helpers
{
    public static class CalendarExtensionMethods
    {
        /// <summary>
        /// One occurrence of a calendar component, as the frame shows it.
        /// <para>
        /// The times are the occurrence's own, from its period, never the component's. A recurring
        /// event's DTSTART and DTEND are those of its first instance: read from there, a daily event
        /// that began two months ago came back dated two months ago - and was sorted and printed that
        /// way by the frame - and for a series written in UTC, the time of day was an hour out
        /// whenever a daylight-saving change lay in between. An event with neither DTEND nor DURATION
        /// has no end of its own at all, and reading one failed its whole feed.
        /// </para>
        /// </summary>
        public static IAppointment ToAppointment(this Occurrence occurrence)
        {
            var period = occurrence.Period;
            var startTime = period.StartTime.AsSystemLocal;

            var appointment = new Appointment
            {
                StartTime = startTime,
                Duration = period.Duration,
                // Ical.Net leaves EndTime null on a to-do's occurrences, and on those of a journal
                // entry with a time of day - with a zero duration, so they end where they start.
                EndTime = period.EndTime?.AsSystemLocal ?? startTime.Add(period.Duration)
            };

            if (occurrence.Source is CalendarEvent calEvent)
            {
                appointment.Summary = calEvent.Summary;
                appointment.Description = calEvent.Description;
                appointment.Location = calEvent.Location;
            }

            return appointment;
        }
    }
}
