import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { ToastService } from '../../core/toast.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [ReactiveFormsModule],
  template: `
    <div class="auth-screen">
      <aside class="auth-aside">
        <div class="row" style="gap: 12px">
          <div class="rail-logo" style="margin: 0">
            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <rect x="2.5" y="4.5" width="19" height="15" rx="2.5" />
              <path d="M3 6l9 7 9-7" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </div>
          <div>
            <div style="font-weight: 700; font-size: 17px">OpenPost</div>
            <div style="font-size: 12px; color: rgba(255,255,255,.6)">Self-hosted mail server</div>
          </div>
        </div>

        <div>
          <h1>Your mail server,<br />on your own terms.</h1>
          <p class="lede">
            A complete SMTP + webmail stack you can run on a single box, inspect line by line, and
            fork without asking anyone.
          </p>

          <div class="auth-features">
            <div class="auth-feature">
              <span class="dot">◆</span>
              <span><strong>No vendor lock-in.</strong> PostgreSQL, .NET 10 and Angular — all open source.</span>
            </div>
            <div class="auth-feature">
              <span class="dot">◆</span>
              <span><strong>Multi-domain.</strong> Host many domains with per-domain DKIM keys and quotas.</span>
            </div>
            <div class="auth-feature">
              <span class="dot">◆</span>
              <span><strong>Deliverability built in.</strong> SPF, DKIM, DMARC guidance and an outbound retry queue.</span>
            </div>
            <div class="auth-feature">
              <span class="dot">◆</span>
              <span><strong>Your data stays.</strong> Mail bodies live on your disk, metadata in your database.</span>
            </div>
          </div>
        </div>

        <div style="font-size: 12px; color: rgba(255,255,255,.55)">
          @if (info(); as meta) {
            {{ meta.product }} v{{ meta.version }} · {{ meta.environment }} · {{ meta.database }}
          } @else {
            OpenPost · open source (MIT)
          }
        </div>
      </aside>

      <div class="auth-form-wrap">
        <div class="auth-form">
          <h2>{{ step() === 'password' ? 'Sign in' : 'Two-factor authentication' }}</h2>
          <p class="sub">
            {{ step() === 'password'
              ? 'Use your mailbox address and password.'
              : 'Enter the 6-digit code from your authenticator app.' }}
          </p>

          <form [formGroup]="form" (ngSubmit)="submit()" class="stack">
            @if (step() === 'password') {
              <div class="field">
                <label for="email">Email address</label>
                <input id="email" class="input" type="email" formControlName="email" autocomplete="username" placeholder="you@example.org" />
              </div>
              <div class="field">
                <label for="password">Password</label>
                <input id="password" class="input" type="password" formControlName="password" autocomplete="current-password" placeholder="••••••••••••" />
              </div>
            } @else {
              <div class="field">
                <label for="totp">Authentication code</label>
                <input id="totp" class="input mono" inputmode="numeric" maxlength="7" formControlName="totpCode" placeholder="000000" autocomplete="one-time-code" />
              </div>
            }

            @if (error()) {
              <div class="badge badge-danger" style="padding: 9px 12px; border-radius: 9px; white-space: normal">
                {{ error() }}
              </div>
            }

            <button class="btn btn-primary" type="submit" [disabled]="form.invalid || loading()" style="height: 42px">
              @if (loading()) {
                <span class="spinner" style="width: 15px; height: 15px; border-color: rgba(255,255,255,.4); border-top-color: #fff"></span>
              }
              {{ step() === 'password' ? 'Sign in' : 'Verify and continue' }}
            </button>

            @if (step() === 'totp') {
              <button class="btn btn-ghost btn-sm" type="button" (click)="reset()">Use a different account</button>
            }
          </form>

          <p class="subtle small" style="margin-top: 26px">
            First run? Sign in with the bootstrap administrator from your
            <span class="mono">Bootstrap__AdminEmail</span> setting.
          </p>
        </div>
      </div>
    </div>
  `,
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly step = signal<'password' | 'totp'>('password');
  readonly info = signal<{ product: string; version: string; environment: string; database: string } | null>(null);

  readonly form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required]],
    totpCode: [''],
  });

  constructor() {
    this.api.systemInfo().subscribe({
      next: (info) => this.info.set(info),
      error: () => this.info.set(null),
    });
  }

  submit(): void {
    if (this.form.invalid || this.loading()) return;
    this.loading.set(true);
    this.error.set(null);

    const { email, password, totpCode } = this.form.getRawValue();
    this.auth.login(email, password, totpCode || undefined).subscribe({
      next: () => {
        this.loading.set(false);
        this.toast.success('Signed in');
        void this.router.navigate(['/mail']);
      },
      error: (err) => {
        this.loading.set(false);
        const apiError = ApiService.toError(err);
        if (apiError.detail === 'totpRequired' || apiError.status === 401 && this.form.value.totpCode === '' && this.step() === 'password') {
          if (apiError.detail === 'totpRequired') {
            this.step.set('totp');
            this.error.set(null);
            return;
          }
        }
        this.error.set(apiError.detail || apiError.title);
      },
    });
  }

  reset(): void {
    this.step.set('password');
    this.form.patchValue({ totpCode: '' });
    this.error.set(null);
  }
}
