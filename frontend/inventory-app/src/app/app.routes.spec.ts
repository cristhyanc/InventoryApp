import { MsalGuard } from '@azure/msal-angular';
import { Route } from '@angular/router';
import { ACCESS_NOT_ALLOWED_PATH } from './access/capability.guard';
import { accountStateGuard } from './access/account-state.guard';
import { platformAdminGuard } from './access/platform-admin.guard';
import { AccessNotAllowedComponent } from './access/access-not-allowed.component';
import { AccessUnavailableComponent } from './access/access-unavailable.component';
import { BusinessApplicationComponent } from './access/business-application.component';
import { InviteAcceptanceComponent } from './access/invite-acceptance.component';
import { inviteTokenAddressBarGuard, pendingInviteTokenGuard } from './access/invite-address-bar.guard';
import { WelcomeComponent } from './access/welcome.component';
import { ApplicationPendingComponent } from './access/application-pending.component';
import { ApplicationRejectedComponent } from './access/application-rejected.component';
import { routes } from './app.routes';
import { SignInComponent } from './auth/sign-in.component';
import { PROVIDER_LOCKED_PATH } from './provider/provider-lock';
import { ProviderLockedComponent } from './provider/provider-locked.component';
import { StockHistoryPageComponent } from './components/stock/stock-history-page.component';
import { AdminImportsComponent } from './components/admin/imports/admin-imports.component';
import { HistoricalCostRecoveryComponent } from './components/admin/historical-cost-recovery/historical-cost-recovery.component';
import { AvcoTransitionComponent } from './components/admin/avco-transition/avco-transition.component';
import { CostingRepairPageComponent } from './components/admin/costing-repair/costing-repair-page.component';
import { HistoricalGstClassificationComponent } from './components/admin/historical-gst-classification/historical-gst-classification.component';
import { NayaxSaleTimestampRepairComponent } from './components/admin/nayax-sale-timestamp-repair/nayax-sale-timestamp-repair.component';
import { PlatformDiagnosticsComponent } from './components/admin/diagnostics/platform-diagnostics.component';
import { SuperAdminBusinessesComponent } from './components/super-admin/businesses/super-admin-businesses.component';
import { SuperAdminSupplierPresetsComponent } from './components/super-admin/supplier-presets/super-admin-supplier-presets.component';
import { AdminUsersComponent } from './components/admin/users/admin-users.component';
import { RolesComponent } from './components/admin/roles/roles.component';
import { BusinessSettingsComponent } from './components/admin/business-settings/business-settings.component';
import { PurchaseEditPageComponent } from './components/purchases/purchase-edit/purchase-edit-page.component';
import { PurchaseListComponent } from './components/purchases/purchase-list.component';
import { PurchaseUploadComponent } from './components/purchases/purchase-upload.component';
import { SupplierOrdersComponent } from './components/purchases/supplier-orders.component';

function route(path: string): Route {
  const match = routes.find((candidate) => candidate.path === path);
  expect(match).toBeDefined();
  return match!;
}

async function loadedComponent(path: string): Promise<unknown> {
  const loader = route(path).loadComponent;
  expect(loader).toBeDefined();
  return loader!();
}

describe('Stock History routes', () => {
  it('serves the global Stock History page at /stock-history, behind authentication', async () => {
    expect(route('stock-history').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('stock-history')).resolves.toBe(StockHistoryPageComponent);
  });

  /**
   * The product "Stock" links in the product lists point at /products/:id/stock. That entry point
   * keeps working and reaches the global page, which preselects the product from the route
   * parameter (see stock-history-page.component.spec.ts).
   */
  it('keeps the legacy product entry point and resolves it to the same page', async () => {
    expect(route('products/:id/stock').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('products/:id/stock')).resolves.toBe(StockHistoryPageComponent);
  });
});

describe('Purchase edit route (issue #475)', () => {
  it('serves the dedicated Edit purchase page at /purchases/:id/edit, behind authentication', async () => {
    expect(route('purchases/:id/edit').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('purchases/:id/edit')).resolves.toBe(PurchaseEditPageComponent);
  });

  /**
   * `purchases/new` and `purchases/orders` are two-segment paths, so the three-segment edit pattern
   * cannot shadow them in either declaration order. This pins that none of the purchase routes
   * started resolving to a different page when the edit route arrived.
   */
  it('leaves the existing purchase routes resolving to their own pages', async () => {
    await expect(loadedComponent('purchases')).resolves.toBe(PurchaseListComponent);
    await expect(loadedComponent('purchases/new')).resolves.toBe(PurchaseUploadComponent);
    await expect(loadedComponent('purchases/orders')).resolves.toBe(SupplierOrdersComponent);
  });
});

describe('Admin Imports route (issue #389)', () => {
  it('serves the dedicated Admin Imports page at /admin/imports, behind authentication', async () => {
    expect(route('admin/imports').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/imports')).resolves.toBe(AdminImportsComponent);
  });
});

describe('Admin Users route (issue #534)', () => {
  it('serves the dedicated Admin Users page at /admin/users, behind authentication', async () => {
    expect(route('admin/users').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/users')).resolves.toBe(AdminUsersComponent);
  });
});

describe('Admin Roles route (issue #530)', () => {
  it('serves the dedicated Admin Roles page at /admin/roles, behind authentication and Roles.View', async () => {
    expect(route('admin/roles').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/roles')).resolves.toBe(RolesComponent);
  });
});

describe('Admin Business Settings route (issue #549)', () => {
  it('serves the dedicated Business Settings page at /admin/business-settings, behind authentication and Business.Manage', async () => {
    expect(route('admin/business-settings').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/business-settings')).resolves.toBe(BusinessSettingsComponent);
  });
});

describe('Admin costing and maintenance routes (issue #390)', () => {
  it('serves the dedicated Nayax Historical Cost Recovery page, behind authentication', async () => {
    expect(route('admin/historical-cost-recovery').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/historical-cost-recovery')).resolves.toBe(HistoricalCostRecoveryComponent);
  });

  it('serves the dedicated Inventory AVCO Transition Baseline page, behind authentication', async () => {
    expect(route('admin/avco-transition').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/avco-transition')).resolves.toBe(AvcoTransitionComponent);
  });

  it('serves the dedicated Costing Repair page, behind authentication', async () => {
    expect(route('admin/costing-repair').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/costing-repair')).resolves.toBe(CostingRepairPageComponent);
  });

  it('serves the dedicated Historical GST Classification page, behind authentication (issue #433)', async () => {
    expect(route('admin/historical-gst-classification').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/historical-gst-classification')).resolves.toBe(
      HistoricalGstClassificationComponent
    );
  });

  /**
   * The Nayax Sale Timestamp Repair page (#487, the UI over #472's Preview/Apply API) is routed
   * like every other Admin maintenance page, so the URL is reachable directly by an authenticated
   * operator; the API authorizes and business-scopes both of its requests itself.
   */
  it('serves the dedicated Nayax Sale Timestamp Repair page, behind authentication (issue #487)', async () => {
    expect(route('admin/nayax-sale-timestamp-repair').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/nayax-sale-timestamp-repair')).resolves.toBe(
      NayaxSaleTimestampRepairComponent
    );
  });

  /**
   * The super-admin diagnostics page (#335), moved under `/super-admin` in issue #546.
   * `platformAdminGuard` asks the same platform-admin check the page itself asks on arrival, so
   * entering the URL directly still reaches a page that shows a refusal, not data, whichever of
   * the two answers first.
   */
  it('serves the Platform Diagnostics page behind the platform-admin guard, not a frontend role (issues #335, #546)', async () => {
    expect(route('super-admin/diagnostics').canActivate).toEqual([MsalGuard, platformAdminGuard]);
    await expect(loadedComponent('super-admin/diagnostics')).resolves.toBe(PlatformDiagnosticsComponent);
  });

  /** The old address (#335) redirects to its new home (#546) rather than serving the page. */
  it('redirects the old /admin/diagnostics address to /super-admin/diagnostics', () => {
    const redirect = route('admin/diagnostics');

    expect(redirect.redirectTo).toBe('/super-admin/diagnostics');
    expect(redirect.pathMatch).toBe('full');
    expect(redirect.canActivate).toBeUndefined();
  });

  /**
   * The retired Reporting Dashboard page (issue #806, parent #803): its distinct capabilities now
   * live on Bookkeeping, so its old address redirects there instead of serving its own page. See
   * `reporting-dashboard-redirect.spec.ts` for the query-preservation and access-control behaviour
   * this wiring produces once the router runs it.
   */
  it('redirects the old /reports address to /reports/bookkeeping, with no guard of its own', () => {
    const redirect = route('reports');

    // A redirect function, not a plain string, so the original query string survives - see the
    // comment beside it in `app.routes.ts` and `reporting-dashboard-redirect.spec.ts`.
    expect(typeof redirect.redirectTo).toBe('function');
    expect(redirect.pathMatch).toBe('full');
    expect(redirect.canActivate).toBeUndefined();
    expect(redirect.loadComponent).toBeUndefined();
  });

  /**
   * `/admin` keeps its own address rather than redirecting: the Dashboard and each dedicated
   * page's "Back to Admin" link still point at it, alongside the sidebar's Admin group (#391).
   */
  it('keeps /admin as the Admin landing page rather than redirecting it', async () => {
    const admin = route('admin');
    expect(admin.redirectTo).toBeUndefined();
    expect(admin.canActivate).toContain(MsalGuard);
  });
});

/**
 * Capability route guards (issue #528). They are presentation only - every endpoint behind every
 * page checks the same capability itself - but a page nobody's role allows must not be left
 * rendering a screen full of refusals, so each authenticated route declares one alongside
 * `MsalGuard`.
 */
describe('capability route guards (issue #528)', () => {
  function authenticatedRoutes(): Route[] {
    return routes.filter((candidate) => (candidate.canActivate ?? []).includes(MsalGuard));
  }

  /**
   * `/access-not-allowed` is where a capability guard *sends* a member, so guarding it by one
   * would send them away from their own explanation, and `/needs-provider` (issue #729) is there
   * for exactly the same reason - with the added one that the destinations redirecting to it need
   * different capabilities, each already checked by the capability guard that ran before
   * `requireProvider`. The account-state routing destinations and
   * the invite route (issue #529) are the other exceptions, with the business application form
   * (issue #543) that those destinations link to, for the same reason `accountStateGuard`
   * is absent from them - see the `account-state routing and the global 403 handler` describe
   * block below. `/super-admin/diagnostics` is decided by the diagnostics API per request rather
   * than by any business role (issue #335) and carries `platformAdminGuard` instead (issue #546,
   * see the describe block below), so it is guarded but not by a capability and does not appear
   * here.
   */
  it('guards every authenticated route with a capability, except the ones that cannot carry one', () => {
    const unguarded = authenticatedRoutes()
      .filter((candidate) => (candidate.canActivate ?? []).length < 2)
      .map((candidate) => candidate.path ?? '');

    expect(unguarded).toEqual([
      ACCESS_NOT_ALLOWED_PATH,
      PROVIDER_LOCKED_PATH,
      'welcome',
      'application-pending',
      'application-rejected',
      'access-unavailable',
      'apply',
      'invite'
    ]);
  });

  it('keeps MsalGuard first on every route it guards, so authentication is still decided first', () => {
    for (const candidate of authenticatedRoutes()) {
      expect((candidate.canActivate ?? [])[0]).toBe(MsalGuard);
    }
  });

  it('serves the access-not-allowed page behind authentication alone', async () => {
    expect(route(ACCESS_NOT_ALLOWED_PATH).canActivate).toContain(MsalGuard);
    await expect(loadedComponent(ACCESS_NOT_ALLOWED_PATH)).resolves.toBe(AccessNotAllowedComponent);
  });

  it('keeps the public Entra callback route free of any guard', () => {
    expect(route('auth').canActivate).toBeUndefined();
  });
});

/**
 * Account-state routing (issue #529): `accountStateGuard` runs before the capability guard on
 * every business route, so the account-state read settles before that page's own business calls
 * ever run, and it is absent from the routes it would otherwise send somebody straight back to.
 */
describe('account-state routing (issue #529)', () => {
  function capabilityGuardedRoutes(): Route[] {
    return routes.filter((candidate) => (candidate.canActivate ?? []).length >= 3);
  }

  const bypassPaths = [
    'welcome',
    'application-pending',
    'application-rejected',
    'access-unavailable',
    'apply',
    'invite'
  ];

  it('puts accountStateGuard second, after MsalGuard and before the capability guard, on every business route', () => {
    for (const candidate of capabilityGuardedRoutes()) {
      const guards = candidate.canActivate ?? [];
      expect(guards[0]).toBe(MsalGuard);
      expect(guards[1]).toBe(accountStateGuard);
    }
  });

  it('carries no accountStateGuard on the account-state destinations or the invite route', () => {
    for (const path of bypassPaths) {
      expect(route(path).canActivate ?? []).not.toContain(accountStateGuard);
    }
  });

  it('serves the welcome, application-pending and application-rejected pages, behind authentication alone (issue #542)', () => {
    expect(route('welcome').canActivate).toEqual([MsalGuard]);
    expect(route('welcome').component).toBe(WelcomeComponent);
    expect(route('application-pending').canActivate).toEqual([MsalGuard]);
    expect(route('application-pending').component).toBe(ApplicationPendingComponent);
    expect(route('application-rejected').canActivate).toEqual([MsalGuard]);
    expect(route('application-rejected').component).toBe(ApplicationRejectedComponent);
  });

  it('serves the invite acceptance page behind authentication alone (issue #535)', async () => {
    expect(route('invite').canActivate).toEqual([MsalGuard]);
    await expect(loadedComponent('invite')).resolves.toBe(InviteAcceptanceComponent);
  });

  it('never activates an invite link itself, so MsalGuard never sees its token (issue #681)', () => {
    expect(route('invite/:token').canMatch).toEqual([inviteTokenAddressBarGuard]);
    expect(route('invite/:token').canActivate).toBeUndefined();
    expect(route('invite').canMatch).toEqual([pendingInviteTokenGuard]);
  });

  it('serves the business application form at /apply, behind authentication alone (issue #543)', () => {
    expect(route('apply').canActivate).toEqual([MsalGuard]);
    expect(route('apply').component).toBe(BusinessApplicationComponent);
  });

  it('serves the real access-unavailable page, behind authentication alone', () => {
    expect(route('access-unavailable').canActivate).toEqual([MsalGuard]);
    expect(route('access-unavailable').component).toBe(AccessUnavailableComponent);
  });
});

/**
 * `/super-admin/*` routing (issue #546). `platformAdminGuard` is neither a capability guard nor
 * `accountStateGuard`: platform-admin authority has nothing to do with business membership or
 * role, so a refusal belongs on the dashboard, never on a membership-handling destination.
 */
describe('super-admin routing (issue #546)', () => {
  it('guards /super-admin/diagnostics with MsalGuard then platformAdminGuard, and nothing else', () => {
    expect(route('super-admin/diagnostics').canActivate).toEqual([MsalGuard, platformAdminGuard]);
  });

  it('carries no accountStateGuard, because platform-admin authority is independent of membership', () => {
    expect(route('super-admin/diagnostics').canActivate ?? []).not.toContain(accountStateGuard);
  });

  /** The Businesses page (issue #547) is guarded exactly like diagnostics, for the same reason. */
  it('guards /super-admin/businesses with MsalGuard then platformAdminGuard, and nothing else', () => {
    expect(route('super-admin/businesses').canActivate).toEqual([MsalGuard, platformAdminGuard]);
  });

  it('carries no accountStateGuard on /super-admin/businesses either', () => {
    expect(route('super-admin/businesses').canActivate ?? []).not.toContain(accountStateGuard);
  });

  it('loads the Super Admin Businesses page component', async () => {
    await expect(loadedComponent('super-admin/businesses')).resolves.toBe(SuperAdminBusinessesComponent);
  });

  /**
   * The Supplier presets page (issue #848) is guarded exactly like the two above, for the same
   * reason: its authority is the platform-admin check and no business role or membership.
   */
  it('guards /super-admin/supplier-presets with MsalGuard then platformAdminGuard, and nothing else', () => {
    expect(route('super-admin/supplier-presets').canActivate).toEqual([MsalGuard, platformAdminGuard]);
  });

  it('carries no accountStateGuard on /super-admin/supplier-presets either', () => {
    expect(route('super-admin/supplier-presets').canActivate ?? []).not.toContain(accountStateGuard);
  });

  it('loads the Super Admin Supplier presets page component', async () => {
    await expect(loadedComponent('super-admin/supplier-presets')).resolves.toBe(SuperAdminSupplierPresetsComponent);
  });
});

/**
 * The live provider routes and the "needs a provider" page (issue #729, child E2 of issue #329).
 *
 * `requireProvider` is presentation exactly as the capability guard is: each endpoint behind these
 * pages resolves the business's own Nayax credentials and answers for itself. What the guard adds is
 * that the URL behaves like the locked menu entry - typing `/machines` with no provider reaches one
 * explanation instead of a page of refusals.
 */
describe('provider-locked routes (issue #729)', () => {
  /**
   * The live provider destinations. `machines` left this list in issue #740: the machine list page
   * now has the business's own machine records to show and its own create/rename/deactivate actions,
   * so it carries no provider guard. The machine **details** page still does - every panel on it is
   * a Nayax read - and giving it a local half belongs to the provider machine review (issue #742).
   */
  const lockedPaths = ['machines/:id', 'sites', 'sites/:id/products', 'reports/site-commissions'];

  /**
   * **Historical reports stay accessible** (decision P5). These read stored sales, so they carry no
   * provider guard at all and open normally with no provider, reporting their own availability
   * states instead (issues #730 and #731). The site commission report is deliberately not here: a
   * site is known only from the live fleet read, so it has nothing stored to report on.
   *
   * `reports` itself is absent since issue #806: it no longer serves a page of its own, so it
   * carries no guard at all - see the "retired Reporting Dashboard redirect" tests below.
   */
  const storedDataReportPaths = [
    'reports/bookkeeping',
    'reports/daily',
    'reports/transactions',
    'reports/reconciliation',
    'reports/machines',
    'reports/products',
    'reports/gst'
  ];

  it('serves the needs-a-provider page behind authentication alone', async () => {
    expect(route(PROVIDER_LOCKED_PATH).canActivate).toEqual([MsalGuard]);
    await expect(loadedComponent(PROVIDER_LOCKED_PATH)).resolves.toBe(ProviderLockedComponent);
  });

  it('guards every live provider route, after its capability guard rather than before it', () => {
    for (const path of lockedPaths) {
      const guards = route(path).canActivate ?? [];

      expect(guards).toHaveLength(4);
      expect(guards[0]).toBe(MsalGuard);
      expect(guards[1]).toBe(accountStateGuard);
      // The capability guard stays third, so a role that does not include the page is sent to its
      // own explanation rather than told to connect a provider it still could not use.
      expect(guards[2]).not.toBe(guards[3]);
    }
  });

  it('leaves every report over stored data with no provider guard at all', () => {
    for (const path of storedDataReportPaths) {
      expect(route(path).canActivate).toHaveLength(3);
    }
  });

  /**
   * The machine list opens with no provider (issue #740): it carries authentication, the account
   * state and the capability, and no provider guard, so a business that never connected Nayax
   * reaches its own machine records rather than the "needs a provider" page.
   */
  it('leaves the machine list with no provider guard, keeping its capability guard', () => {
    const guards = route('machines').canActivate ?? [];

    expect(guards).toHaveLength(3);
    expect(guards[0]).toBe(MsalGuard);
    expect(guards[1]).toBe(accountStateGuard);
  });

  /**
   * The provider repair and admin tools are hidden from the menu (issue #729) and no more than
   * that: their URLs keep working exactly as before, because each one's API authorizes and answers
   * for itself and page behaviour is out of this issue's scope.
   */
  it('leaves the Nayax imports and the sale timestamp repair routes unguarded by the provider', () => {
    expect(route('admin/imports').canActivate).toHaveLength(3);
    expect(route('admin/nayax-sale-timestamp-repair').canActivate).toHaveLength(3);
  });

  it('keeps the Integrations page reachable, because it is where a connection is made', () => {
    expect(route('admin/nayax-settings').canActivate).toHaveLength(3);
  });
});

/**
 * The sign-in page (issue #711). It is the page `SignInAuthorityGuard` sends a signed-out visitor
 * to instead of starting a redirect, so it has to be reachable without authentication - exactly
 * like the `/auth` callback, and for the same reason.
 */
describe('sign-in routing (issue #711)', () => {
  it('serves the sign-in page publicly, with no guard at all', () => {
    expect(route('sign-in').component).toBe(SignInComponent);
    expect(route('sign-in').canActivate).toBeUndefined();
    expect(route('sign-in').canMatch).toBeUndefined();
  });

  it('keeps the MSAL redirect callback public beside it', () => {
    expect(route('auth').canActivate).toBeUndefined();
  });

  /**
   * Both are declared before the wildcard, which would otherwise swallow them and land a visitor
   * on the dashboard they are not signed in for.
   */
  it('declares both authentication pages before the wildcard', () => {
    const wildcard = routes.findIndex((candidate) => candidate.path === '**');

    expect(routes.findIndex((candidate) => candidate.path === 'sign-in')).toBeLessThan(wildcard);
    expect(routes.findIndex((candidate) => candidate.path === 'auth')).toBeLessThan(wildcard);
  });
});
