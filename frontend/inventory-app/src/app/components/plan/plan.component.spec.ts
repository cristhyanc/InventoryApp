import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { AccessService, MemberAccess, MemberAccessState } from '../../access/access.service';
import { Capability, capabilitySet } from '../../access/capabilities';
import { Entitlement } from '../../access/entitlement';
import { PlanCatalogueEntry, PlanCatalogueService } from '../../access/plan-catalogue.service';
import { RolePreviewService } from '../../role-preview/role-preview.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { PlanComponent } from './plan.component';

const ENFORCED_PAID: Entitlement = {
  enforced: true,
  plan: 'Pro',
  source: 'BillingSubscription',
  endsAtUtc: null,
  attachmentStorage: { usedBytes: 2_000_000_000, limitBytes: 20_000_000_000, warningLevel: 'Normal' },
  connectedMachines: {
    includedByPlan: 5,
    purchasedAdditional: 0,
    allowance: 5,
    count: 4,
    asOfDateUtc: '2026-10-29T00:00:00Z',
    ageInDays: 0,
    state: 'Current',
    unavailableReason: 'None',
    machinesOverAllowance: 0
  }
};

const NOT_ENFORCED: Entitlement = {
  enforced: false,
  plan: null,
  source: 'EnforcementDisabled',
  endsAtUtc: null,
  attachmentStorage: null,
  connectedMachines: null
};

const CATALOGUE: readonly PlanCatalogueEntry[] = [
  {
    plan: 'Free',
    monthlyPriceMinorUnits: 0,
    currencyCode: 'AUD',
    attachmentStorageLimitBytes: 1_000_000_000,
    includedConnectedMachines: 0,
    additionalConnectedMachinePriceMinorUnits: null
  },
  {
    plan: 'Starter',
    monthlyPriceMinorUnits: 700,
    currencyCode: 'AUD',
    attachmentStorageLimitBytes: 20_000_000_000,
    includedConnectedMachines: 0,
    additionalConnectedMachinePriceMinorUnits: null
  },
  {
    plan: 'Pro',
    monthlyPriceMinorUnits: 1500,
    currencyCode: 'AUD',
    attachmentStorageLimitBytes: 20_000_000_000,
    includedConnectedMachines: 5,
    additionalConnectedMachinePriceMinorUnits: 200
  }
];

function granted(
  role: string | null,
  capabilities: readonly Capability[],
  entitlement: Entitlement | undefined = ENFORCED_PAID
): MemberAccessState {
  const access: MemberAccess = {
    role,
    capabilities: capabilitySet([...capabilities]),
    businessName: 'Vending Co',
    timeZoneId: 'Australia/Sydney',
    entitlement
  };
  return { status: 'granted', access };
}

const OWNER_CAPABILITIES: readonly Capability[] = [
  'Dashboard.View',
  'Dashboard.ViewFinancials',
  'PickList.View',
  'Inventory.Operate',
  'Purchasing.Operate'
];

const OPERATOR_CAPABILITIES: readonly Capability[] = ['Dashboard.View', 'PickList.View', 'Inventory.Operate'];

async function render(options: {
  access?: Observable<MemberAccessState>;
  catalogue?: Observable<readonly PlanCatalogueEntry[]>;
  supportSessionActive?: Observable<boolean>;
  rolePreviewActive?: Observable<boolean>;
} = {}) {
  await TestBed.configureTestingModule({
    imports: [PlanComponent],
    providers: [
      { provide: AccessService, useValue: { access$: options.access ?? of(granted('Owner', OWNER_CAPABILITIES)) } },
      { provide: PlanCatalogueService, useValue: { get: () => options.catalogue ?? of(CATALOGUE) } },
      { provide: SupportSessionService, useValue: { active$: options.supportSessionActive ?? of(false) } },
      { provide: RolePreviewService, useValue: { active$: options.rolePreviewActive ?? of(false) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(PlanComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function byTestId(host: HTMLElement, id: string): HTMLElement | null {
  return host.querySelector(`[data-testid="${id}"]`);
}

afterEach(() => TestBed.resetTestingModule());

describe('PlanComponent loading and failure (issue #970)', () => {
  it('shows a loading message while the capability read has not answered', async () => {
    const { host } = await render({ access: of({ status: 'unread' }) });

    expect(byTestId(host, 'plan-loading')).not.toBeNull();
    expect(byTestId(host, 'plan-current')?.textContent).not.toContain('Pro');
  });

  it('shows an unavailable message when the capability read failed', async () => {
    const { host } = await render({ access: of({ status: 'unavailable' }) });

    expect(byTestId(host, 'plan-unavailable')).not.toBeNull();
  });

  it('shows an unavailable message when the API did not report an entitlement at all', async () => {
    const access: MemberAccess = {
      role: 'Owner',
      capabilities: capabilitySet([...OWNER_CAPABILITIES]),
      businessName: 'Vending Co',
      timeZoneId: 'Australia/Sydney'
    };
    const { host } = await render({ access: of({ status: 'granted', access }) });

    expect(byTestId(host, 'plan-unavailable')).not.toBeNull();
  });

  it('shows a catalogue load error when the catalogue read fails', async () => {
    const { host } = await render({ catalogue: throwError(() => new Error('boom')) });

    expect(byTestId(host, 'plan-catalogue-load-error')).not.toBeNull();
    expect(host.querySelector('table')).toBeNull();
  });
});

describe('PlanComponent enforcement (AC7)', () => {
  it('says no plan limits apply while enforcement is disabled', async () => {
    const { host } = await render({ access: of(granted('Owner', OWNER_CAPABILITIES, NOT_ENFORCED)) });

    expect(byTestId(host, 'plan-not-enforced')?.textContent).toContain('No plan limits currently apply');
    expect(byTestId(host, 'plan-name')).toBeNull();
  });

  it('shows the effective plan once enforcement applies', async () => {
    const { host } = await render();

    expect(byTestId(host, 'plan-name')?.textContent).toBe('Pro');
  });
});

describe('PlanComponent source labels (AC1, AC5)', () => {
  const cases: Array<[string, string]> = [
    ['EnforcementDisabled', 'Plan limits are not switched on'],
    ['GrandfatheringNotCompleted', 'grandfathered'],
    ['ManualGrant', 'Manual grant'],
    ['BillingSubscription', 'Paid subscription'],
    ['NoActiveRecord', 'No active subscription']
  ];

  it.each(cases)('labels %s in plain language', async (source, expectedText) => {
    const entitlement: Entitlement = { ...ENFORCED_PAID, source, enforced: source !== 'EnforcementDisabled' && source !== 'GrandfatheringNotCompleted' };
    const { host } = await render({ access: of(granted('Owner', OWNER_CAPABILITIES, entitlement)) });

    const text = (byTestId(host, 'plan-source') ?? byTestId(host, 'plan-not-enforced'))?.textContent ?? '';
    expect(text).toContain(expectedText);
  });

  it('never labels ManualGrant as complimentary or grandfathered', async () => {
    const { host } = await render({
      access: of(granted('Owner', OWNER_CAPABILITIES, { ...ENFORCED_PAID, source: 'ManualGrant' }))
    });

    const text = byTestId(host, 'plan-source')?.textContent ?? '';
    expect(text).toBe('Manual grant');
    expect(text).not.toContain('complimentary');
    expect(text).not.toContain('grandfathered');
  });

  it('never labels NoActiveRecord as cancelled or expired', async () => {
    const { host } = await render({
      access: of(granted('Owner', OWNER_CAPABILITIES, { ...ENFORCED_PAID, source: 'NoActiveRecord' }))
    });

    const text = byTestId(host, 'plan-source')?.textContent ?? '';
    expect(text).not.toContain('cancelled');
    expect(text).not.toContain('expired');
  });

  it('falls back to the raw name for a source this build does not recognise', async () => {
    const { host } = await render({
      access: of(granted('Owner', OWNER_CAPABILITIES, { ...ENFORCED_PAID, source: 'SomeFutureSource' }))
    });

    expect(byTestId(host, 'plan-source')?.textContent).toContain('SomeFutureSource');
  });
});

describe('PlanComponent storage (AC1)', () => {
  it('shows no warning or full message under 80%', async () => {
    const { host } = await render();

    expect(byTestId(host, 'plan-storage-warning')).toBeNull();
    expect(byTestId(host, 'plan-storage-full')).toBeNull();
  });

  it('shows the warning message at 80% and above', async () => {
    const entitlement: Entitlement = {
      ...ENFORCED_PAID,
      attachmentStorage: { usedBytes: 16_000_000_000, limitBytes: 20_000_000_000, warningLevel: 'Warning' }
    };
    const { host } = await render({ access: of(granted('Owner', OWNER_CAPABILITIES, entitlement)) });

    expect(byTestId(host, 'plan-storage-warning')?.textContent).toContain('80%');
    expect(byTestId(host, 'plan-storage-full')).toBeNull();
  });

  it('shows the full message at 100%, never deleting anything or blocking downloads by implication', async () => {
    const entitlement: Entitlement = {
      ...ENFORCED_PAID,
      attachmentStorage: { usedBytes: 20_000_000_000, limitBytes: 20_000_000_000, warningLevel: 'Full' }
    };
    const { host } = await render({ access: of(granted('Owner', OWNER_CAPABILITIES, entitlement)) });

    expect(byTestId(host, 'plan-storage-full')?.textContent).toContain('full');
    expect(byTestId(host, 'plan-storage-warning')).toBeNull();
  });

  it('renders an accessible progress bar stating the real percentage', async () => {
    const { host } = await render();

    const bar = byTestId(host, 'plan-storage')?.querySelector('[role="progressbar"]');
    expect(bar?.getAttribute('aria-valuenow')).toBe('10');
    expect(bar?.getAttribute('aria-valuemin')).toBe('0');
    expect(bar?.getAttribute('aria-valuemax')).toBe('100');
    expect(bar?.getAttribute('aria-valuetext')).toContain('10%');
  });
});

describe('PlanComponent connected machines (AC1)', () => {
  it('shows the count against the allowance when current', async () => {
    const { host } = await render();

    expect(byTestId(host, 'plan-machines')?.textContent).toContain('4 of 5 included');
    expect(byTestId(host, 'plan-machines-stale')).toBeNull();
  });

  it('marks a stale count with its age', async () => {
    const entitlement: Entitlement = {
      ...ENFORCED_PAID,
      connectedMachines: { ...ENFORCED_PAID.connectedMachines!, state: 'Stale', ageInDays: 3 }
    };
    const { host } = await render({ access: of(granted('Owner', OWNER_CAPABILITIES, entitlement)) });

    expect(byTestId(host, 'plan-machines-stale')?.textContent).toContain('3d old');
  });

  it('shows an unavailable count with its reason, never a zero', async () => {
    const entitlement: Entitlement = {
      ...ENFORCED_PAID,
      connectedMachines: {
        ...ENFORCED_PAID.connectedMachines!,
        count: null,
        state: 'Unavailable',
        unavailableReason: 'ConnectionNeedsAttention'
      }
    };
    const { host } = await render({ access: of(granted('Owner', OWNER_CAPABILITIES, entitlement)) });

    const text = byTestId(host, 'plan-machines-unavailable')?.textContent ?? '';
    expect(text).toContain('Count unavailable');
    expect(text).toContain('needs attention');
    expect(text).not.toContain('0 of');
  });

  it('notes machines over the allowance as billed, never blocked', async () => {
    const entitlement: Entitlement = {
      ...ENFORCED_PAID,
      connectedMachines: { ...ENFORCED_PAID.connectedMachines!, count: 6, machinesOverAllowance: 1 }
    };
    const { host } = await render({ access: of(granted('Owner', OWNER_CAPABILITIES, entitlement)) });

    expect(byTestId(host, 'plan-machines-over-allowance')?.textContent).toContain('billed, never blocked');
  });
});

describe('PlanComponent role-based action (AC4)', () => {
  it('shows the Owner a coming-soon message', async () => {
    const { host } = await render({ access: of(granted('Owner', OWNER_CAPABILITIES)) });

    expect(byTestId(host, 'plan-action-owner')?.textContent).toContain('coming soon');
    expect(byTestId(host, 'plan-action-other')).toBeNull();
    expect(host.querySelector('button')).toBeNull();
  });

  it('tells another role to ask their Owner', async () => {
    const { host } = await render({ access: of(granted('Operator', OPERATOR_CAPABILITIES)) });

    expect(byTestId(host, 'plan-action-other')?.textContent).toContain('Ask your business Owner');
    expect(byTestId(host, 'plan-action-owner')).toBeNull();
  });

  it('shows no action at all inside a support session, whatever role is being viewed', async () => {
    const { host } = await render({
      access: of(granted('Owner', OWNER_CAPABILITIES)),
      supportSessionActive: of(true)
    });

    expect(byTestId(host, 'plan-action')).toBeNull();
  });

  it('shows no action at all during a role preview', async () => {
    const { host } = await render({
      access: of(granted('Owner', OWNER_CAPABILITIES)),
      rolePreviewActive: of(true)
    });

    expect(byTestId(host, 'plan-action')).toBeNull();
  });
});

describe('PlanComponent plan comparison (AC2, AC3)', () => {
  it('renders every catalogue plan with no hard-coded price', async () => {
    const { host } = await render();

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows).toHaveLength(3);
    expect(rows[0].textContent).toContain('Free');
    expect(rows[0].textContent).toContain('Free');
    expect(rows[2].textContent).toContain('Pro');
    expect(rows[2].textContent).toContain('$15.00 AUD/mo');
  });

  it('shows the additional-machine rate to a role with Dashboard.ViewFinancials', async () => {
    const { host } = await render({ access: of(granted('Owner', OWNER_CAPABILITIES)) });

    expect(byTestId(host, 'plan-catalogue-additional-machine-header')).not.toBeNull();
    const cells = host.querySelectorAll('[data-testid="plan-catalogue-additional-machine-cell"]');
    expect(cells[2].textContent).toContain('$2.00 AUD/mo each');
  });

  it('hides the additional-machine rate from a role without Dashboard.ViewFinancials', async () => {
    const { host } = await render({ access: of(granted('Operator', OPERATOR_CAPABILITIES)) });

    expect(byTestId(host, 'plan-catalogue-additional-machine-header')).toBeNull();
    expect(host.querySelectorAll('[data-testid="plan-catalogue-additional-machine-cell"]')).toHaveLength(0);
  });

  it('shows the plan prices and storage to every role, financial capability or not', async () => {
    const { host } = await render({ access: of(granted('Operator', OPERATOR_CAPABILITIES)) });

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows[2].textContent).toContain('$15.00 AUD/mo');
    expect(rows[2].textContent).toContain('20 GB');
  });
});
