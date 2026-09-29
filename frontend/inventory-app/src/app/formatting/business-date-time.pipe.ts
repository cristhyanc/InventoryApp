import { Pipe, PipeTransform } from '@angular/core';

/**
 * The InventoryApp business timezone used for operator-facing date/time display.
 * `Australia/Canberra` is an IANA identifier, so `Intl.DateTimeFormat` resolves AEST/AEDT
 * from the platform timezone database instead of a fixed UTC offset.
 */
export const BUSINESS_TIME_ZONE = 'Australia/Canberra';

const businessDateTimeFormatter = new Intl.DateTimeFormat('en-AU', {
  timeZone: BUSINESS_TIME_ZONE,
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hour12: true
});

@Pipe({
  name: 'businessDateTime',
  standalone: true
})
export class BusinessDateTimePipe implements PipeTransform {
  transform(value: string | Date | null | undefined): string {
    if (value === null || value === undefined || value === '') {
      return '';
    }
    const date = value instanceof Date ? value : new Date(value);
    if (Number.isNaN(date.getTime())) {
      return '';
    }
    return businessDateTimeFormatter.format(date);
  }
}
