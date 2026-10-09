using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Models;

namespace ImmichFrame.Core.Helpers
{
    public static class CalendarExtensionMethods
    {
        /// <summary>
        /// The occurrences of the calendar's events, to-dos and journal entries on <paramref name="day"/>,
        /// a midnight on this machine's clock: from it to the next one.
        /// <para>
        /// Not <c>Calendar.GetOccurrences</c>, which reads the window it is handed in the zone of each
        /// component's own DTSTART - it relabels the two midnights with that zone and converts neither -
        /// and so answered for anything written in another zone with that zone's day rather than this
        /// one. A frame in Los Angeles, where the UTC day ends in the late afternoon, lost every event
        /// written in UTC after that and was shown the previous evening's in their place. Each component
        /// is asked here for this machine's day in its own zone instead, which is no more to expand than
        /// before: one day per component.
        /// </para>
        /// <para>
        /// What GetOccurrences does besides is done here as Ical.Net 4.3.1 does it: an instance of a
        /// series that a component with a RECURRENCE-ID replaces - a meeting moved for one week - gives
        /// way to the replacement rather than being shown alongside it.
        /// </para>
        /// </summary>
        public static HashSet<Occurrence> GetOccurrencesOn(this Calendar calendar, DateTime day)
        {
            var nextDay = day.AddDays(1);
            var occurrences = new HashSet<Occurrence>(calendar.RecurringItems
                .OfType<IRecurringComponent>()
                .SelectMany(component => component.GetOccurrences(InZoneOf(component, day), InZoneOf(component, nextDay))));

            var replaced = occurrences
                .Where(occurrence => occurrence.Source is UniqueComponent)
                .GroupBy(occurrence => ((UniqueComponent)occurrence.Source).Uid)
                .SelectMany(series => series
                    .Where(replacement => replacement.Source.RecurrenceId != null)
                    .SelectMany(replacement => series.Where(occurrence =>
                        occurrence.Source.RecurrenceId == null
                        && replacement.Source.RecurrenceId.Date.Equals(occurrence.Period.StartTime.Date))))
                .ToList();

            occurrences.ExceptWith(replaced);
            return occurrences;
        }

        /// <summary>
        /// <paramref name="local"/>, a time on this machine's clock, as the clock of the zone
        /// <paramref name="component"/>'s DTSTART is written in reads at that moment - which is how
        /// Ical.Net takes a bound it is handed. A floating time is read on this machine's clock already.
        /// A date alone, an all-day event's, names a day rather than a moment, as
        /// <see cref="ToAppointment"/> shows it, so it is asked for by its date - even with a TZID, which
        /// RFC 5545 does not allow on a date and a feed may carry regardless: converted, today's bounds
        /// would reach into the neighbouring days there.
        /// </summary>
        private static IDateTime InZoneOf(IRecurringComponent component, DateTime local)
        {
            var bound = new CalDateTime(local);

            return component.Start is { HasTime: true } start && !string.IsNullOrWhiteSpace(start.TzId)
                ? bound.ToTimeZone(start.TzId)
                : bound;
        }

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
            var startTime = OnThisClock(period.StartTime);
            var endTime = period.EndTime is { } periodEnd ? OnThisClock(periodEnd) : (DateTime?)null;

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

        /// <summary>
        /// <paramref name="time"/> as this machine's clock shows it.
        /// <para>
        /// A time of day names a moment, and is converted to it: through UTC, from the zone a TZID gives
        /// it, from UTC itself, or - floating, with no zone - from this machine's own. Ical.Net's
        /// AsSystemLocal converts nothing for a TZID: it stamps the time as written with this machine's
        /// offset, so nine o'clock in New York was shown at nine o'clock wherever the frame hung. A date
        /// alone, an all-day event's, names a day rather than a moment, and stays that day's midnight.
        /// </para>
        /// </summary>
        private static DateTime OnThisClock(IDateTime time) =>
            time.HasTime ? time.AsUtc.ToLocalTime() : time.AsSystemLocal;
    }
}
