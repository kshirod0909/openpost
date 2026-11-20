import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { InitialsPipe } from '../../core/pipes';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, InitialsPipe],
  template: `
    <div class="shell">
      <nav class="rail">
        <div class="rail-logo" title="OpenPost">
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <rect x="2.5" y="4.5" width="19" height="15" rx="2.5" />
            <path d="M3 6l9 7 9-7" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
        </div>

        <a class="rail-btn" routerLink="/mail" routerLinkActive="active" title="Mail" aria-label="Mail">
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8">
            <path d="M4 5h16v14H4z" /><path d="M4 7l8 6 8-6" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
        </a>

        @if (isAdmin()) {
          <a class="rail-btn" routerLink="/admin" routerLinkActive="active" title="Administration" aria-label="Administration">
            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8">
              <path d="M12 3l7 3v6c0 4.5-3 7.5-7 9-4-1.5-7-4.5-7-9V6l7-3z" stroke-linejoin="round" />
            </svg>
          </a>
        }

        <a class="rail-btn" routerLink="/settings" routerLinkActive="active" title="Settings" aria-label="Settings">
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8">
            <circle cx="12" cy="12" r="3.2" />
            <path d="M19.4 15a1.7 1.7 0 00.3 1.9l.1.1a2 2 0 11-2.8 2.8l-.1-.1a1.7 1.7 0 00-1.9-.3 1.7 1.7 0 00-1 1.5V21a2 2 0 11-4 0v-.1a1.7 1.7 0 00-1.1-1.5 1.7 1.7 0 00-1.9.3l-.1.1a2 2 0 11-2.8-2.8l.1-.1a1.7 1.7 0 00.3-1.9 1.7 1.7 0 00-1.5-1H3a2 2 0 110-4h.1a1.7 1.7 0 001.5-1.1 1.7 1.7 0 00-.3-1.9l-.1-.1a2 2 0 112.8-2.8l.1.1a1.7 1.7 0 001.9.3H9a1.7 1.7 0 001-1.5V3a2 2 0 114 0v.1a1.7 1.7 0 001 1.5 1.7 1.7 0 001.9-.3l.1-.1a2 2 0 112.8 2.8l-.1.1a1.7 1.7 0 00-.3 1.9V9a1.7 1.7 0 001.5 1H21a2 2 0 110 4h-.1a1.7 1.7 0 00-1.5 1z" stroke-linejoin="round" />
          </svg>
        </a>

        <div class="rail-spacer"></div>

        <button class="rail-btn" (click)="toggleTheme()" [title]="dark() ? 'Switch to light theme' : 'Switch to dark theme'" aria-label="Toggle theme">
          @if (dark()) {
            <svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8">
              <circle cx="12" cy="12" r="4" />
              <path d="M12 2v2M12 20v2M2 12h2M20 12h2M5 5l1.5 1.5M17.5 17.5L19 19M19 5l-1.5 1.5M6.5 17.5L5 19" stroke-linecap="round" />
            </svg>
          } @else {
            <svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8">
              <path d="M20 14.5A8.5 8.5 0 019.5 4a8.5 8.5 0 1010.5 10.5z" stroke-linejoin="round" />
            </svg>
          }
        </button>

        <button class="rail-btn" (click)="menuOpen.set(!menuOpen())" title="Account" aria-label="Account">
          <span class="avatar" style="width: 32px; height: 32px; font-size: 11.5px">{{ displayName() | initials: email() }}</span>
        </button>
      </nav>

      <div class="main">
        <header class="topbar">
          <div class="grow row" style="gap: 10px; min-width: 0">
            <span style="font-weight: 650">{{ title() }}</span>
            @if (primaryMailbox(); as mailbox) {
              <span class="badge badge-brand">{{ mailbox.address }}</span>
            }
          </div>

          <span class="badge">{{ environmentLabel() }}</span>

          <a class="btn btn-ghost btn-icon" routerLink="/settings" title="Account settings" aria-label="Account settings">
            <span class="avatar" style="width: 28px; height: 28px; font-size: 10.5px">{{ displayName() | initials: email() }}</span>
          </a>
        </header>

        <div class="content">
          <router-outlet />
        </div>
      </div>

      @if (menuOpen()) {
        <div class="modal-backdrop" (click)="menuOpen.set(false)">
          <div class="card" style="width: 300px; padding: 8px" (click)="$event.stopPropagation()">
            <div style="padding: 12px 12px 14px">
              <div style="font-weight: 650">{{ displayName() }}</div>
              <div class="muted small">{{ email() }}</div>
              <div class="row" style="gap: 6px; margin-top: 8px">
                @for (role of roles(); track role) {
                  <span class="badge badge-info">{{ role }}</span>
                }
              </div>
            </div>
            <div class="divider" style="margin: 0 0 6px"></div>
            <a class="btn btn-ghost" routerLink="/settings" style="width: 100%; justify-content: flex-start" (click)="menuOpen.set(false)">Account settings</a>
            <button class="btn btn-ghost" style="width: 100%; justify-content: flex-start" (click)="logout()">Sign out</button>
          </div>
        </div>
      }
    </div>
  `,
})
export class ShellComponent {
  private readonly auth = inject(AuthService);

  readonly menuOpen = signal(false);
  readonly dark = signal(document.documentElement.getAttribute('data-theme') === 'dark');

  readonly displayName = computed(() => this.auth.user()?.displayName ?? '');
  readonly email = computed(() => this.auth.user()?.email ?? '');
  readonly roles = computed(() => this.auth.user()?.roles ?? []);
  readonly isAdmin = this.auth.isAdmin;
  readonly primaryMailbox = computed(() => this.auth.mailboxes()[0] ?? null);
  readonly environmentLabel = computed(() => (this.roles().includes('SystemAdmin') ? 'System administrator' : 'Member'));

  readonly title = computed(() => {
    const url = location.pathname;
    if (url.startsWith('/admin')) return 'Administration';
    if (url.startsWith('/settings')) return 'Settings';
    return 'Mail';
  });

  constructor() {
    const stored = localStorage.getItem('openpost.theme');
    const theme = stored ?? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
    this.applyTheme(theme);
  }

  toggleTheme(): void {
    const next = this.dark() ? 'light' : 'dark';
    this.applyTheme(next);
    localStorage.setItem('openpost.theme', next);
  }

  private applyTheme(theme: string): void {
    document.documentElement.setAttribute('data-theme', theme);
    this.dark.set(theme === 'dark');
  }

  logout(): void {
    this.menuOpen.set(false);
    this.auth.logout();
  }
}
