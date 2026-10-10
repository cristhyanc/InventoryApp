import { Pipe, PipeTransform } from '@angular/core';
import { BusinessTimeZoneService } from './business-time-zone.service';

/**
 * Renders a true instant as the operator's business wall-clock time, in the current business's own
 * IANA time zone (issue #499; the zone was a fixed `Australia/Canberra` constant before).
 *
 * With no zone it renders nothing rather than guessing one: formatting in the browser's accidental
 * local zone - or in a fixed Australian one for a business that is not in Australia - would show a
 * confident timestamp that is simply wrong. The ordinary loading state is handled before any
 * timestamp reaches this pipe: the shell renders no routed page until the business lookup has
 * settled (`AppComponent`), so a page that is on screen has the zone. Rendering nothing is
 * therefore the failure state - the lookup could not be made at all - and not a flicker a pure
 * pipe would get stuck on.
 */
@Pipe({
  name: 'businessDateTime',
  standalone: true
})
export class BusinessDateTimePipe implements PipeTransform {
  private formatter?: Intl.DateTimeFormat;
  private formatterTimeZone?: string;

  constructor(private readonly timeZone: BusinessTimeZoneService) {}

  transform(value: string | Date | null | undefined): string {
    if (value === null || value === undefined || value === '') {
      return '';
    }
    const timeZoneId = this.timeZone.timeZoneId;
    if (timeZoneId === null) {
      return '';
    }
    const date = value instanceof Date ? value : new Date(value);
    if (Number.isNaN(date.getTime())) {
      return '';
    }
    return this.formatterFor(timeZoneId).format(date);
  }

  /** One formatter per zone: building an `Intl.DateTimeFormat` per cell is needlessly expensive. */
  private formatterFor(timeZoneId: string): Intl.DateTimeFormat {
    if (this.formatter === undefined || this.formatterTimeZone !== timeZoneId) {
      this.formatterTimeZone = timeZoneId;
      this.formatter = new Intl.DateTimeFormat('en-AU', {
        timeZone: timeZoneId,
        day: '2-digit',
        month: '2-digit',
        year: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
        hour12: true
      });
    }
    return this.formatter;
  }
}
