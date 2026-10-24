import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';
import { OptionVisibility } from '../../../access/option-visibility';
import { OptionVisibilityService } from '../../../services/option-visibility.service';
import { HistoricalGstVisibilityToggleComponent } from './historical-gst-visibility-toggle.component';

interface Rendered {
  readonly fixture: ComponentFixture<HistoricalGstVisibilityToggleComponent>;
  readonly host: HTMLElement;
  readonly save: jest.Mock;
  readonly saved: boolean[];
}

async function render(
  visible: boolean,
  save: jest.Mock = jest.fn((requested: boolean) =>
    of<OptionVisibility>({ historicalGstClassificationVisible: requested })
  )
): Promise<Rendered> {
  await TestBed.configureTestingModule({
    imports: [HistoricalGstVisibilityToggleComponent],
    providers: [
      { provide: OptionVisibilityService, useValue: { setHistoricalGstClassificationVisible: save } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(HistoricalGstVisibilityToggleComponent);
  fixture.componentInstance.visible = visible;

  const saved: boolean[] = [];
  fixture.componentInstance.saved.subscribe((value) => saved.push(value));
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, save, saved };
}

function control(host: HTMLElement): HTMLButtonElement {
  return host.querySelector<HTMLButtonElement>('button')!;
}

/**
 * The Super Admin's switch (issue #873). What it has to prove is the lifecycle the hub does not own:
 * what is submitted, what is reported, and - the criterion this component exists for - that a
 * refused save leaves the setting reading exactly as the server still holds it.
 */
describe('HistoricalGstVisibilityToggleComponent (issue #873)', () => {
  it('offers to hide the option while it is shown', async () => {
    const { host } = await render(true);

    expect(control(host).textContent).toContain('Hide Historical GST Classification from other users');
  });

  it('offers to show the option while it is hidden', async () => {
    const { host } = await render(false);

    expect(control(host).textContent).toContain('Show Historical GST Classification to other users');
  });

  it('says that a Super Admin keeps the tool in both states', async () => {
    const { host } = await render(false);

    expect(host.textContent).toContain('hidden from everybody but a Super Admin');
    expect(host.textContent).toContain('Super Admins always see it and keep their existing access');
  });

  it('submits the opposite of the stored state and reports what the server stored', async () => {
    const { host, save, saved } = await render(true);

    control(host).click();

    expect(save).toHaveBeenCalledWith(false);
    expect(saved).toEqual([false]);
  });

  it('reports the server answer rather than the requested value', async () => {
    // A server that answered differently from the request is the one case where reporting the
    // requested value would publish a setting nobody stored.
    const save = jest.fn(() => of<OptionVisibility>({ historicalGstClassificationVisible: true }));
    const { host, saved } = await render(true, save);

    control(host).click();

    expect(saved).toEqual([true]);
  });

  it('disables the control and says so while the save is in flight, and submits once', async () => {
    const pending = new Subject<OptionVisibility>();
    const save = jest.fn((): Observable<OptionVisibility> => pending);
    const { host, fixture } = await render(true, save);

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
    const { host, fixture, saved } = await render(true, save);

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
    const { host, fixture, saved } = await render(true, save);

    control(host).click();
    fixture.detectChanges();
    expect(host.querySelector('[role="alert"]')).not.toBeNull();

    control(host).click();
    fixture.detectChanges();

    expect(host.querySelector('[role="alert"]')).toBeNull();
    expect(saved).toEqual([false]);
  });
});
