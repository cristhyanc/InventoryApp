import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';
import { OptionVisibility } from '../../../access/option-visibility';
import { OptionVisibilityService } from '../../../services/option-visibility.service';
import {
  HistoricalGstVisibilityToggleComponent,
  SwitchableOptionDisplay
} from './historical-gst-visibility-toggle.component';

interface Rendered {
  readonly fixture: ComponentFixture<HistoricalGstVisibilityToggleComponent>;
  readonly host: HTMLElement;
  readonly save: jest.Mock;
  readonly saved: boolean[];
  readonly retried: number[];
}

async function render(
  state: SwitchableOptionDisplay,
  save: jest.Mock = jest.fn((requested: boolean) =>
    of<OptionVisibility>({ historicalGstClassificationVisible: requested })
  ),
  inputs: { refreshing?: boolean; refreshFailed?: boolean } = {}
): Promise<Rendered> {
  await TestBed.configureTestingModule({
    imports: [HistoricalGstVisibilityToggleComponent],
    providers: [
      { provide: OptionVisibilityService, useValue: { setHistoricalGstClassificationVisible: save } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(HistoricalGstVisibilityToggleComponent);
  fixture.componentInstance.state = state;
  fixture.componentInstance.refreshing = inputs.refreshing ?? false;
  fixture.componentInstance.refreshFailed = inputs.refreshFailed ?? false;

  const saved: boolean[] = [];
  const retried: number[] = [];
  fixture.componentInstance.saved.subscribe((value) => saved.push(value));
  fixture.componentInstance.retry.subscribe(() => retried.push(1));
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, save, saved, retried };
}

function control(host: HTMLElement): HTMLButtonElement {
  return host.querySelector<HTMLButtonElement>('#historical-gst-classification-visibility')!;
}

function retryControl(host: HTMLElement): HTMLButtonElement | null {
  return host.querySelector<HTMLButtonElement>('#historical-gst-classification-visibility-retry');
}

/**
 * The Super Admin's switch (issue #873). What it has to prove is the lifecycle the hub does not own:
 * what is submitted, what is reported, and - the criterion this component exists for - that a
 * refused save leaves the setting reading exactly as the server still holds it.
 */
describe('HistoricalGstVisibilityToggleComponent (issue #873)', () => {
  it('offers to hide the option while it is shown', async () => {
    const { host } = await render('on');

    expect(control(host).textContent).toContain('Hide Historical GST Classification from other users');
  });

  it('offers to show the option while it is hidden', async () => {
    const { host } = await render('off');

    expect(control(host).textContent).toContain('Show Historical GST Classification to other users');
  });

  it('says that a Super Admin keeps the tool in both states', async () => {
    const { host } = await render('off');

    expect(host.textContent).toContain('hidden from everybody but a Super Admin');
    expect(host.textContent).toContain('Super Admins always see it and keep their existing access');
  });

  it('submits the opposite of the stored state and reports what the server stored', async () => {
    const { host, save, saved } = await render('on');

    control(host).click();

    expect(save).toHaveBeenCalledWith(false);
    expect(saved).toEqual([false]);
  });

  it('reports the server answer rather than the requested value', async () => {
    // A server that answered differently from the request is the one case where reporting the
    // requested value would publish a setting nobody stored.
    const save = jest.fn(() => of<OptionVisibility>({ historicalGstClassificationVisible: true }));
    const { host, saved } = await render('on', save);

    control(host).click();

    expect(saved).toEqual([true]);
  });

  it('disables the control and says so while the save is in flight, and submits once', async () => {
    const pending = new Subject<OptionVisibility>();
    const save = jest.fn((): Observable<OptionVisibility> => pending);
    const { host, fixture } = await render('on', save);

    control(host).click();
    fixture.detectChanges();

    expect(control(host).disabled).toBe(true);
    expect(host.querySelector('[role="status"]')?.textContent).toContain('Saving');

    // A second click while the first is in flight is not a second write.
    control(host).click();
    expect(save).toHaveBeenCalledTimes(1);

    pending.next({ historicalGstClassificationVisible: false });
    fixture.detectChanges();
    expect(control(host).disabled).toBe(false);
    expect(host.querySelector('[role="status"]')).toBeNull();
  });

  /**
   * The criterion: "a failed save must not leave the UI claiming that an unsaved setting is
   * active". Nothing is reported to the hub, the control still offers the same action over the same
   * stored state, and the refusal is announced.
   */
  it('reports nothing and keeps the stored state when the save is refused', async () => {
    const save = jest.fn(() => throwError(() => new Error('refused')));
    const { host, fixture, saved } = await render('on', save);

    control(host).click();
    fixture.detectChanges();

    expect(saved).toEqual([]);
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('was not saved');
    expect(control(host).textContent).toContain('Hide Historical GST Classification from other users');
    expect(control(host).disabled).toBe(false);
  });

  it('clears a previous failure when the next save is attempted', async () => {
    const save = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new Error('refused')))
      .mockReturnValueOnce(of<OptionVisibility>({ historicalGstClassificationVisible: false }));
    const { host, fixture, saved } = await render('on', save);

    control(host).click();
    fixture.detectChanges();
    expect(host.querySelector('[role="alert"]')).not.toBeNull();

    control(host).click();
    fixture.detectChanges();

    expect(host.querySelector('[role="alert"]')).toBeNull();
    expect(saved).toEqual([false]);
  });

  /**
   * An unknown setting is not an `On` setting (repair of the review's [P2] finding). Neither of the
   * two "nobody has said" answers may be rendered as a state, offered as a switchable one, or
   * submitted from: the opposite of a value this control invented is not what the administrator
   * asked for.
   */
  describe('an answer the server has not given', () => {
    it('offers no switch at all while the setting is still being read, and says so', async () => {
      const { host, save } = await render('unread');

      expect(control(host)).toBeNull();
      expect(retryControl(host)).toBeNull();
      expect(host.textContent).toContain('still being read');
      expect(host.textContent).not.toContain('shown to every user whose role allows it');
      expect(host.querySelector('[role="status"]')?.textContent).toContain('Reading the stored setting');
      expect(save).not.toHaveBeenCalled();
    });

    it('offers reading the setting again, and no switch, when it could not be read', async () => {
      const { host, retried } = await render('unavailable');

      expect(control(host)).toBeNull();
      expect(host.textContent).toContain('could not be read');
      expect(host.textContent).not.toContain('shown to every user whose role allows it');

      retryControl(host)!.click();
      expect(retried).toEqual([1]);
    });

    it('submits nothing when asked to apply an unknown setting', async () => {
      const { fixture, save, saved } = await render('unavailable');

      fixture.componentInstance.apply();

      expect(save).not.toHaveBeenCalled();
      expect(saved).toEqual([]);
    });

    it('keeps the stored state, reports the failed re-read and offers reading it again', async () => {
      const { host } = await render('off', undefined, { refreshFailed: true });

      // The stored answer still reads exactly as the server last gave it - no `On` claim - and the
      // next switch would ask for the other state.
      expect(host.textContent).toContain('hidden from everybody but a Super Admin');
      expect(control(host).textContent).toContain('Show Historical GST Classification to other users');
      expect(host.querySelector('[role="alert"]')?.textContent).toContain('could not be read just now');
      expect(retryControl(host)).not.toBeNull();
    });

    it('says the setting is being read while the re-read is in flight, without blocking the switch', async () => {
      const { host } = await render('off', undefined, { refreshing: true });

      expect(host.querySelector('[role="status"]')?.textContent).toContain('Reading the stored setting');
      expect(control(host).disabled).toBe(false);
      expect(control(host).textContent).toContain('Show Historical GST Classification to other users');
    });
  });
});
