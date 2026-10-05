import { Component, ElementRef, EventEmitter, HostListener, Input, Output, ViewChild } from '@angular/core';

/**
 * The signed-in user control in the top-right of the application header (issue #391).
 *
 * It is presentation only: the shell keeps the MSAL identity and the redirect calls, and this
 * component shows the active account and reports the two intents the application actually has.
 * There is deliberately no profile or account destination here, because no such page exists.
 */
@Component({
  selector: 'app-user-menu',
  standalone: true,
  template: `
    @if (isSignedIn) {
      <div class="relative">
        <button
          #trigger
          type="button"
          class="flex max-w-56 items-center gap-2 rounded-full border border-slate-300 bg-white py-1 pl-1 pr-3 text-sm text-slate-700 transition hover:bg-slate-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          aria-haspopup="true"
          [attr.aria-expanded]="isOpen"
          [attr.aria-controls]="isOpen ? panelId : null"
          [attr.aria-label]="displayName + ' account menu'"
          (click)="toggle()"
        >
          @if (initials) {
            <span
              aria-hidden="true"
              class="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-blue-600 text-xs font-semibold text-white"
              >{{ initials }}</span
            >
          }
          <span class="truncate">{{ displayName }}</span>
          <span aria-hidden="true" class="text-xs text-slate-400">&#9662;</span>
        </button>

        @if (isOpen) {
          <div
            [id]="panelId"
            role="group"
            aria-label="Account"
            class="absolute right-0 z-40 mt-2 w-60 rounded-lg border border-slate-200 bg-white p-2 text-sm shadow-lg"
          >
            <p class="px-3 py-2 text-slate-500">
              Signed in as
              <span class="block truncate font-medium text-slate-700">{{ displayName }}</span>
            </p>
            <div class="my-1 border-t border-slate-100"></div>
            <button
              type="button"
              class="w-full rounded-md px-3 py-2 text-left text-slate-700 transition hover:bg-slate-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              (click)="requestSignOut()"
            >
              Sign out
            </button>
          </div>
        }
      </div>
    } @else {
      <button
        type="button"
        class="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        (click)="signInRequested.emit()"
      >
        Sign in
      </button>
    }
  `
})
export class UserMenuComponent {
  @Input({ required: true }) isSignedIn = false;

  /** The MSAL account's display name or username; empty when neither is available. */
  @Input() userName = '';

  @Output() readonly signInRequested = new EventEmitter<void>();
  @Output() readonly signOutRequested = new EventEmitter<void>();

  readonly panelId = 'user-menu-panel';

  isOpen = false;

  @ViewChild('trigger') private readonly trigger?: ElementRef<HTMLButtonElement>;

  constructor(private readonly elementRef: ElementRef<HTMLElement>) {}

  get displayName(): string {
    return this.userName.trim() || 'Signed-in user';
  }

  get initials(): string {
    return this.userName
      .trim()
      .split(/\s+/)
      .filter((part) => part.length > 0)
      .slice(0, 2)
      .map((part) => part[0].toUpperCase())
      .join('');
  }

  toggle(): void {
    this.isOpen = !this.isOpen;
  }

  requestSignOut(): void {
    this.isOpen = false;
    this.signOutRequested.emit();
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (this.isOpen && !this.elementRef.nativeElement.contains(event.target as Node)) {
      this.isOpen = false;
    }
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.isOpen) {
      this.isOpen = false;
      this.trigger?.nativeElement.focus();
    }
  }
}
