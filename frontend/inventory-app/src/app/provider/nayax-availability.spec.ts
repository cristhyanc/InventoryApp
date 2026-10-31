import { HttpErrorResponse } from '@angular/common/http';
import {
  nayaxUnavailabilityOfError,
  nayaxUnavailabilityOfReason,
  nayaxUnavailableMessage,
  storageOnlyReorderLabel
} from './nayax-availability';

/**
 * The two per-feature Nayax availability answers (issue #727). What matters here is what is *not*
 * one of them: a real outage, a cancellation and a caller's own authorization failure are errors,
 * and presenting one of them as "Nayax is not connected" would describe a connection nobody tested.
 */

describe('nayaxUnavailabilityOfReason', () => {
  it('recognises the three published reason codes', () => {
    expect(nayaxUnavailabilityOfReason('NayaxNotConnected')).toBe('NayaxNotConnected');
    expect(nayaxUnavailabilityOfReason('PermissionNotGranted')).toBe('PermissionNotGranted');
    expect(nayaxUnavailabilityOfReason('PlanRequired')).toBe('PlanRequired');
  });

  it('does not treat a support session as a statement about the connection', () => {
    expect(nayaxUnavailabilityOfReason('SupportView')).toBeNull();
  });

  it('guesses nothing from an unrecognised, absent or empty reason', () => {
    expect(nayaxUnavailabilityOfReason('TokenExpired')).toBeNull();
    expect(nayaxUnavailabilityOfReason('')).toBeNull();
    expect(nayaxUnavailabilityOfReason(null)).toBeNull();
    expect(nayaxUnavailabilityOfReason(undefined)).toBeNull();
  });
});

describe('nayaxUnavailabilityOfError', () => {
  function conflict(body: unknown): HttpErrorResponse {
    return new HttpErrorResponse({ status: 409, error: body });
  }

  it('reads the two stable 409 codes', () => {
    expect(nayaxUnavailabilityOfError(conflict({ code: 'nayax_not_connected' }))).toBe('NayaxNotConnected');
    expect(nayaxUnavailabilityOfError(conflict({ code: 'nayax_permission_not_granted' }))).toBe(
      'PermissionNotGranted'
    );
  });

  /**
   * The connection and the plan refusal share the exact same `nayax_not_connected` code (issue
   * #967): `nayaxUnavailableReason` on the body is the only thing that tells them apart.
   */
  it('reads a plan refusal off the shared nayax_not_connected code by its own reason', () => {
    expect(
      nayaxUnavailabilityOfError(conflict({ code: 'nayax_not_connected', nayaxUnavailableReason: 'PlanRequired' }))
    ).toBe('PlanRequired');
  });

  it('reads the connection reason for the shared code when no reason is published, or an unrecognised one is', () => {
    expect(nayaxUnavailabilityOfError(conflict({ code: 'nayax_not_connected' }))).toBe('NayaxNotConnected');
    expect(
      nayaxUnavailabilityOfError(
        conflict({ code: 'nayax_not_connected', nayaxUnavailableReason: 'SomethingNewAndUnrecognised' })
      )
    ).toBe('NayaxNotConnected');
  });

  it('reads no availability answer out of any other status', () => {
    expect(
      nayaxUnavailabilityOfError(new HttpErrorResponse({ status: 502, error: { code: 'nayax_not_connected' } }))
    ).toBeNull();
    expect(nayaxUnavailabilityOfError(new HttpErrorResponse({ status: 403 }))).toBeNull();
    expect(nayaxUnavailabilityOfError(new HttpErrorResponse({ status: 0 }))).toBeNull();
  });

  it('reads no availability answer out of a 409 that carries another code or none', () => {
    expect(nayaxUnavailabilityOfError(conflict({ code: 'domain_conflict' }))).toBeNull();
    expect(nayaxUnavailabilityOfError(conflict({}))).toBeNull();
    expect(nayaxUnavailabilityOfError(conflict(null))).toBeNull();
  });

  /** A body that never parsed as JSON is a string; reading `.code` off it must not throw. */
  it('survives a 409 whose body is not an object', () => {
    expect(nayaxUnavailabilityOfError(conflict('Conflict'))).toBeNull();
  });

  it('is not fooled by a plain error that merely looks like one', () => {
    expect(nayaxUnavailabilityOfError(new Error('nayax_not_connected'))).toBeNull();
    expect(nayaxUnavailabilityOfError({ status: 409, error: { code: 'nayax_not_connected' } })).toBeNull();
    expect(nayaxUnavailabilityOfError(undefined)).toBeNull();
  });
});

describe('nayaxUnavailableMessage', () => {
  it('names the feature it is about, because availability is per feature', () => {
    expect(nayaxUnavailableMessage('NayaxNotConnected', 'Machine refill status')).toBe(
      'Machine refill status is unavailable because Nayax is not connected.'
    );
    expect(nayaxUnavailableMessage('PermissionNotGranted', 'The Machines section')).toBe(
      'The Machines section is unavailable because Nayax has not granted this application permission to read it.'
    );
  });

  /**
   * The plan refusal names no connection problem at all (issue #967/#979): the business's saved
   * credentials, if it has any, are untouched, so the message says what would actually open the
   * feature rather than inviting a reconnection that would not.
   */
  it('names the Pro plan, and says saved credentials are unchanged, for a plan refusal', () => {
    expect(nayaxUnavailableMessage('PlanRequired', 'Sites')).toBe(
      'Sites needs the Pro plan. Any Nayax credentials this business has saved are unchanged.'
    );
  });
});

describe('storageOnlyReorderLabel', () => {
  const scope = "Storage stock only: these figures do not include the machines' current replenishment need";

  it('states the scope and the reason', () => {
    expect(storageOnlyReorderLabel('NayaxNotConnected')).toBe(`${scope}, because Nayax is not connected.`);
    expect(storageOnlyReorderLabel('PermissionNotGranted')).toBe(
      `${scope}, because Nayax has not granted this application permission to read the machines.`
    );
  });

  it('states the scope alone when no reason was published, claiming no cause', () => {
    expect(storageOnlyReorderLabel(null)).toBe(`${scope}.`);
  });

  it('names the plan for a business whose plan does not include the Nayax connection', () => {
    expect(storageOnlyReorderLabel('PlanRequired')).toBe(
      `${scope}, because this business's plan does not include the Nayax connection.`
    );
  });
});
