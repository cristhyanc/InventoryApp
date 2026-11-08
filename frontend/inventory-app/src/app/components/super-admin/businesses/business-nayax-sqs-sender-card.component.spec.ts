import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { BusinessNayaxSqsSenderCardComponent } from './business-nayax-sqs-sender-card.component';
import {
  SuperAdminNayaxSqsSender,
  SuperAdminNayaxSqsSenderService
} from '../../../services/super-admin-nayax-sqs-sender.service';

const recorded: SuperAdminNayaxSqsSender = {
  businessId: 9,
  awsSenderId: 'AIDAEXAMPLEUSERID123',
  isEnabled: true,
  lastMessageReceivedAtUtc: '2026-11-05T22:10:00Z',
  lastReconciliationSucceededAtUtc: '2026-11-05T21:00:00Z',
  readerHealth: {
    lastSuccessfulPollUtc: '2026-11-05T22:11:40Z',
    consecutivePollFailures: 0,
    deadLetterQueueDepth: 0
  },
  revision: 3,
  createdAtUtc: '2026-11-01T01:00:00Z',
  updatedAtUtc: '2026-11-06T03:45:00Z'
};

const removed: SuperAdminNayaxSqsSender = {
  ...recorded,
  awsSenderId: null,
  isEnabled: false,
  lastMessageReceivedAtUtc: null,
  revision: 4
};

function notFound(): HttpErrorResponse {
  return new HttpErrorResponse({ status: 404, statusText: 'Not Found' });
}

function conflict(message: string): HttpErrorResponse {
  return new HttpErrorResponse({ status: 409, statusText: 'Conflict', error: { message } });
}

function badRequest(message: string): HttpErrorResponse {
  return new HttpErrorResponse({ status: 400, statusText: 'Bad Request', error: { message } });
}

async function render(service: Partial<SuperAdminNayaxSqsSenderService>) {
  await TestBed.configureTestingModule({
    imports: [BusinessNayaxSqsSenderCardComponent],
    providers: [{ provide: SuperAdminNayaxSqsSenderService, useValue: service }]
  }).compileComponents();

  const fixture = TestBed.createComponent(BusinessNayaxSqsSenderCardComponent);
  fixture.componentRef.setInput('businessId', 9);
  fixture.componentRef.setInput('businessName', 'Snack Co');
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function click(host: HTMLElement, testId: string): void {
  const button = host.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement | null;
  expect(button).not.toBeNull();
  button!.click();
}

function text(host: HTMLElement, testId: string): string {
  return (host.querySelector(`[data-testid="${testId}"]`)?.textContent ?? '').trim();
}

function type(host: HTMLElement, value: string, fixture: { detectChanges: () => void }): void {
  const field = host.querySelector('#aws-sender-id') as HTMLInputElement;
  field.value = value;
  field.dispatchEvent(new Event('input'));
  fixture.detectChanges();
}

describe('BusinessNayaxSqsSenderCardComponent (issue #1101)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows the recorded sender, whether it is on, and the last message in Sydney time', async () => {
    const { host } = await render({ get: () => of(recorded) });

    expect(text(host, 'current-sender')).toBe('AIDAEXAMPLEUSERID123');
    expect(text(host, 'current-status')).toBe('On');
    // 2026-11-05T22:10Z is 09:10 on 6 November in Sydney (AEDT), which is the point of naming the
    // zone rather than using the browser's.
    expect(text(host, 'last-message')).toContain('06/11/2026');
    expect(text(host, 'last-message')).toContain('9:10');
  });

  /**
   * The reader health and the business's own last successful reconciliation (issue #1106). They are
   * on this card because together with an enabled sender they are exactly what decides whether that
   * business's Dashboard stops calling Nayax on load - so a recorded sender that is having no such
   * effect can be explained from here.
   */
  it('shows the reader health and the last successful reconciliation in Sydney time', async () => {
    const { host } = await render({ get: () => of(recorded) });

    // 2026-11-05T21:00Z is 08:00 on 6 November in Sydney (AEDT).
    expect(text(host, 'last-reconciliation')).toContain('06/11/2026');
    expect(text(host, 'last-reconciliation')).toContain('8:00');

    // 22:11:40Z is 09:11 on the 6th.
    expect(text(host, 'reader-last-poll')).toContain('06/11/2026');
    expect(text(host, 'reader-last-poll')).toContain('9:11');

    expect(text(host, 'reader-failures')).toBe('0');
    expect(text(host, 'dead-letter-depth')).toBe('0');
  });

  /**
   * A reader that has never polled, failures piling up, no dead-letter depth read and a business
   * whose reconciliation has never succeeded: each is reported as what it is, and the depth reads
   * "Unknown" rather than zero - which would say the opposite of what is known.
   */
  it('reports an unstarted reader and a failing one without inventing a zero', async () => {
    const { host } = await render({
      get: () =>
        of({
          ...recorded,
          lastReconciliationSucceededAtUtc: null,
          readerHealth: {
            lastSuccessfulPollUtc: null,
            consecutivePollFailures: 3,
            deadLetterQueueDepth: null
          }
        })
    });

    expect(text(host, 'last-reconciliation')).toBe('Never');
    expect(text(host, 'reader-last-poll')).toBe('Never');
    expect(text(host, 'reader-failures')).toBe('3');
    expect(text(host, 'dead-letter-depth')).toBe('Unknown');
  });

  it('shows None and Never for a business nothing has been recorded for', async () => {
    const { host } = await render({ get: () => throwError(() => notFound()) });

    expect(text(host, 'current-sender')).toBe('None');
    expect(text(host, 'current-status')).toBe('Off');
    expect(text(host, 'last-message')).toBe('Never');
    expect(text(host, 'last-reconciliation')).toBe('Never');
    expect(text(host, 'reader-last-poll')).toBe('Never');
    expect(text(host, 'dead-letter-depth')).toBe('Unknown');
    // A 404 is not an error here, so no load failure is shown.
    expect(host.textContent).not.toContain('Unable to load');
  });

  it('tells the administrator how to find the ID with the AWS CLI', async () => {
    const { host } = await render({ get: () => of(recorded) });

    const help = text(host, 'sender-id-help');
    expect(help).toContain('aws iam get-user --user-name <user> --query User.UserId');
    expect(help).toContain('CloudShell');
    // And says plainly that it is not a credential.
    expect(help).toContain('never an access key');
  });

  it('creates the first row with a null expected revision', async () => {
    const set = jest.fn().mockReturnValue(of(recorded));
    const { host, fixture } = await render({ get: () => throwError(() => notFound()), set });

    type(host, '  AIDAEXAMPLEUSERID123  ', fixture);
    click(host, 'save-sender');

    expect(set).toHaveBeenCalledWith(9, 'AIDAEXAMPLEUSERID123', true, null);
  });

  it('sends the revision the card was loaded with when changing the sender', async () => {
    const set = jest.fn().mockReturnValue(of({ ...recorded, awsSenderId: 'AIDAEXAMPLEUSERID456', revision: 4 }));
    const { host, fixture } = await render({ get: () => of(recorded), set });

    type(host, 'AIDAEXAMPLEUSERID456', fixture);
    click(host, 'save-sender');

    expect(set).toHaveBeenCalledWith(9, 'AIDAEXAMPLEUSERID456', true, 3);
  });

  /** Turning off keeps the id: "off" and "removed" are different decisions. */
  it('turns the mapping off and keeps the recorded sender', async () => {
    const set = jest.fn().mockReturnValue(of({ ...recorded, isEnabled: false, revision: 4 }));
    const { host } = await render({ get: () => of(recorded), set });

    click(host, 'turn-off-sender');

    expect(set).toHaveBeenCalledWith(9, 'AIDAEXAMPLEUSERID123', false, 3);
  });

  it('asks for confirmation before removing, and says the ID becomes free', async () => {
    const set = jest.fn().mockReturnValue(of(removed));
    const { host, fixture } = await render({ get: () => of(recorded), set });

    click(host, 'remove-sender');
    fixture.detectChanges();

    const confirmation = text(host, 'remove-confirmation');
    expect(confirmation).toContain('free for another business');
    expect(confirmation).toContain('history are kept');
    // Nothing is sent until the confirmation is accepted.
    expect(set).not.toHaveBeenCalled();

    click(host, 'confirm-remove');
    expect(set).toHaveBeenCalledWith(9, null, false, 3);
  });

  it('cancelling the removal sends nothing and returns to the card', async () => {
    const set = jest.fn();
    const { host, fixture } = await render({ get: () => of(recorded), set });

    click(host, 'remove-sender');
    fixture.detectChanges();
    click(host, 'cancel-remove');
    fixture.detectChanges();

    expect(set).not.toHaveBeenCalled();
    expect(text(host, 'current-sender')).toBe('AIDAEXAMPLEUSERID123');
  });

  /**
   * Clearing the field and pressing Change is a removal: it releases a globally unique sender id and
   * (once #1103 reads the queue) stops that business's sales being attributed. It therefore reaches
   * the same confirmation the Remove button does, and sends nothing before it is accepted.
   */
  it('asks for the removal confirmation when a cleared field is submitted over a recorded sender', async () => {
    const set = jest.fn().mockReturnValue(of(removed));
    const { host, fixture } = await render({ get: () => of(recorded), set });

    type(host, '   ', fixture);
    click(host, 'save-sender');
    fixture.detectChanges();

    expect(set).not.toHaveBeenCalled();
    expect(text(host, 'remove-confirmation')).toContain('free for another business');

    click(host, 'confirm-remove');
    expect(set).toHaveBeenCalledWith(9, null, false, 3);
  });

  it('cancelling that confirmation leaves the recorded sender and the field untouched', async () => {
    const set = jest.fn();
    const { host, fixture } = await render({ get: () => of(recorded), set });

    type(host, '', fixture);
    click(host, 'save-sender');
    fixture.detectChanges();
    click(host, 'cancel-remove');
    fixture.detectChanges();
    // `ngModel` writes the restored value into the field on a microtask, so the field is read after
    // the fixture settles rather than in the same turn as the click.
    await fixture.whenStable();

    expect(set).not.toHaveBeenCalled();
    expect(text(host, 'current-sender')).toBe('AIDAEXAMPLEUSERID123');
    expect((host.querySelector('#aws-sender-id') as HTMLInputElement).value).toBe('AIDAEXAMPLEUSERID123');
  });

  /** With nothing recorded there is nothing to remove, so an empty submission writes no empty row. */
  it('asks for an ID instead of writing when a cleared field is submitted with nothing recorded', async () => {
    const set = jest.fn();
    const { host, fixture } = await render({ get: () => throwError(() => notFound()), set });

    click(host, 'save-sender');
    fixture.detectChanges();

    expect(set).not.toHaveBeenCalled();
    expect(host.textContent).toContain('Enter an AWS sender ID');
    expect(host.querySelector('[data-testid="remove-confirmation"]')).toBeNull();
    expect(text(host, 'current-sender')).toBe('None');
  });

  it('takes the new state and the new revision from the server’s answer', async () => {
    const set = jest.fn().mockReturnValue(of(removed));
    const { host, fixture } = await render({ get: () => of(recorded), set });

    click(host, 'remove-sender');
    fixture.detectChanges();
    click(host, 'confirm-remove');
    fixture.detectChanges();

    expect(text(host, 'current-sender')).toBe('None');
    expect(text(host, 'current-status')).toBe('Off');

    // The next write presents the revision that answer carried, not the one the card loaded with.
    type(host, 'AIDAEXAMPLEUSERID789', fixture);
    click(host, 'save-sender');
    expect(set).toHaveBeenLastCalledWith(9, 'AIDAEXAMPLEUSERID789', true, 4);
  });

  it('reloads the card and reports the server’s sentence on a 409', async () => {
    const get = jest
      .fn()
      .mockReturnValueOnce(of(recorded))
      .mockReturnValueOnce(of({ ...recorded, awsSenderId: 'AIDAELSEWHEREID12345', revision: 7 }));
    const set = jest
      .fn()
      .mockReturnValue(throwError(() => conflict('This business’s Nayax SQS sender has changed since it was read.')));

    const { host, fixture } = await render({ get, set });

    type(host, 'AIDAEXAMPLEUSERID456', fixture);
    click(host, 'save-sender');
    fixture.detectChanges();

    expect(host.textContent).toContain('has changed since it was read');
    // Reloaded: the card now shows what the other administrator committed.
    expect(get).toHaveBeenCalledTimes(2);
    expect(text(host, 'current-sender')).toBe('AIDAELSEWHEREID12345');

    // And the next write presents the reloaded revision rather than the stale one.
    click(host, 'save-sender');
    expect(set).toHaveBeenLastCalledWith(9, 'AIDAELSEWHEREID12345', true, 7);
  });

  it('shows the server’s refusal verbatim for an invalid ID and does not reload', async () => {
    const get = jest.fn().mockReturnValue(of(recorded));
    const set = jest
      .fn()
      .mockReturnValue(throwError(() =>
        badRequest('An AWS sender ID must be 16 to 128 characters long and contain only letters and digits.')));

    const { host, fixture } = await render({ get, set });

    type(host, 'AIDASHORT', fixture);
    click(host, 'save-sender');
    fixture.detectChanges();

    expect(host.textContent).toContain('16 to 128 characters');
    // A 400 is the caller's own mistake, so the card keeps what was typed rather than reloading.
    expect(get).toHaveBeenCalledTimes(1);
  });

  it('reports a load failure that is not a 404', async () => {
    const { host } = await render({
      get: () => throwError(() => new HttpErrorResponse({ status: 500, statusText: 'Server Error' }))
    });

    expect(host.textContent).toContain('Unable to load');
  });

  it('emits the committed sender so the page can report it', async () => {
    const { component, host, fixture } = await render({
      get: () => of(recorded),
      set: () => of(removed)
    });

    const emitted: SuperAdminNayaxSqsSender[] = [];
    component.saved.subscribe((sender) => emitted.push(sender));

    click(host, 'remove-sender');
    fixture.detectChanges();
    click(host, 'confirm-remove');

    expect(emitted).toEqual([removed]);
  });

  it('offers no Remove or Turn off action when nothing is recorded', async () => {
    const { host } = await render({ get: () => throwError(() => notFound()) });

    expect(host.querySelector('[data-testid="remove-sender"]')).toBeNull();
    expect(host.querySelector('[data-testid="turn-off-sender"]')).toBeNull();
    expect(host.querySelector('[data-testid="save-sender"]')).not.toBeNull();
  });
});
