import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RolesComponent } from './roles.component';
import { CAPABILITY_DESCRIPTIONS, ROLE_DEFINITIONS } from './role-definitions';

async function render() {
  await TestBed.configureTestingModule({
    imports: [RolesComponent],
    providers: [provideRouter([])]
  }).compileComponents();

  const fixture = TestBed.createComponent(RolesComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

/**
 * The Admin → Roles page (issue #530). It is read-only and composed from one frontend definition
 * (`role-definitions.ts`, whose own spec is the drift test); this spec is the per-role content
 * and the absence of any editing control.
 */
describe('RolesComponent (issue #530)', () => {
  it('renders a section for Owner, Manager and Operator, in that order', async () => {
    const { host } = await render();

    const headings = Array.from(host.querySelectorAll('.card-title')).map((el) => el.textContent?.trim());
    expect(headings).toEqual(['Owner', 'Manager', 'Operator']);
  });

  it("renders each role's own capabilities and no other role's", async () => {
    const { host } = await render();
    const text = host.textContent ?? '';

    for (const definition of ROLE_DEFINITIONS) {
      for (const capability of definition.capabilities) {
        expect(text).toContain(CAPABILITY_DESCRIPTIONS[capability]);
      }
    }

    // Operator holds none of these, so their descriptions must appear only in Owner's and
    // Manager's sections.
    const operatorOnlyMissing = ['Integration.Manage', 'Data.Repair', 'Members.Manage', 'Roles.View', 'Business.Manage'] as const;
    const operatorSection = host.querySelectorAll('.card')[2];
    for (const capability of operatorOnlyMissing) {
      expect(operatorSection.textContent ?? '').not.toContain(CAPABILITY_DESCRIPTIONS[capability]);
    }
  });

  it('offers no edit, no form control and no action - this page only explains', async () => {
    const { host } = await render();

    expect(host.querySelectorAll('button, input, select, textarea, form')).toHaveLength(0);
  });

  it('links back to the Admin hub', async () => {
    const { host } = await render();

    expect(host.querySelector('a[routerLink="/admin"]')).not.toBeNull();
  });
});
