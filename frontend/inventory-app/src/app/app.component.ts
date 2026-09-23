import { Component, OnDestroy, OnInit } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AuthenticationResult, InteractionStatus } from '@azure/msal-browser';
import { Subject, filter, takeUntil } from 'rxjs';
import { ToastContainerComponent } from "./components/shared/toast-container.component";
import { LoadingIndicatorComponent } from './components/shared/loading-indicator.component';
import { loginRequest } from './auth-config';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, ToastContainerComponent, LoadingIndicatorComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss'
})
export class AppComponent implements OnInit, OnDestroy {
  title = 'Inventory Manager';

  isLoggedIn = false;
  userName = '';

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly authService: MsalService,
    private readonly msalBroadcastService: MsalBroadcastService
  ) {}

  ngOnInit(): void {
    this.authService.handleRedirectObservable()
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: (result: AuthenticationResult | null) => {
          if (result?.account) {
            this.authService.instance.setActiveAccount(result.account);
          }
          this.updateLoginState();
        },
        error: (error) => {
          console.error('Authentication redirect failed', error);
        }
      });

    // Do not inspect accounts until MSAL has finished any authentication interaction.
    this.msalBroadcastService.inProgress$
      .pipe(
        filter((status: InteractionStatus) => status === InteractionStatus.None),
        takeUntil(this.destroying$)
      )
      .subscribe(() => {
        this.ensureActiveAccount();
        this.updateLoginState();
      });
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  login(): void {
    this.authService.loginRedirect({ ...loginRequest });
  }

  logout(): void {
    const account = this.authService.instance.getActiveAccount();
    this.authService.logoutRedirect({
      account: account ?? undefined,
      postLogoutRedirectUri: window.location.origin
    });
  }

  closeReportsMenu(event: Event): void {
    const target = event.target;
    if (target instanceof HTMLElement) {
      target.closest('details')?.removeAttribute('open');
    }
  }

  private ensureActiveAccount(): void {
    if (this.authService.instance.getActiveAccount()) {
      return;
    }

    const [firstAccount] = this.authService.instance.getAllAccounts();
    if (firstAccount) {
      this.authService.instance.setActiveAccount(firstAccount);
    }
  }

  private updateLoginState(): void {
    const account = this.authService.instance.getActiveAccount();
    this.isLoggedIn = account !== null;
    this.userName = account?.name ?? account?.username ?? '';
  }
}
