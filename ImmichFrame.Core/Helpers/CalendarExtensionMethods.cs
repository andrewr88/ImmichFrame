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
            var endTime = period.EndTime?.AsSystemLocal;

            // Ical.Net leaves EndTime null on a to-do's occurrences, and on those of a journal entry
            // with a time of day, and makes it the start itself on those of an event with neither
            // DTEND nor DURATION - so an all-day event of that kind ended the moment it began, and
            // the frame printed it as 00:00 - 00:00. RFC 5545 gives such an all-day event the day it
            // falls on, and one with a time of day no length at all. Ical.Net's Duration says a day
            // for both a date and a time of exactly midnight, so the date-or-time of the start
            // decides, not the duration.
            var end = endTime is { } e && e > startTime ? e
                : period.StartTime.HasTime ? startTime
                : startTime.Add(period.Duration);

            var appointment = new Appointment
            {
                StartTime = startTime,
                Duration = end - startTime,
                EndTime = end
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
