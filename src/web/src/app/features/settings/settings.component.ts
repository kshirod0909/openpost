import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { FileSizePipe, InitialsPipe } from '../../core/pipes';
import { ToastService } from '../../core/toast.service';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [FormsModule, FileSizePipe, InitialsPipe],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Settings</h1>
          <p>Your profile, security and mailbox details.</p>
        </div>
      </div>

      <div class="grid-2" style="align-items: start">
        <div class="stack">
          <div class="card">
            <div class="card-header"><div class="card-title">Profile</div></div>
            <div class="card-body stack">
              <div class="row" style="gap: 12px">
                <span class="avatar avatar-lg">{{ displayName() | initials: email() }}</span>
                <div>
                  <div style="font-weight: 650">{{ email() }}</div>
                  <div class="row" style="gap: 6px; margin-top: 4px">
                    @for (role of roles(); track role) { <span class="badge badge-info">{{ role }}</span> }
                  </div>
                </div>
              </div>

              <div class="field">
                <label for="displayName">Display name</label>
                <input id="displayName" class="input" [(ngModel)]="displayNameInput" />
              </div>

              <div class="field">
                <label for="signature">Signature</label>
                <textarea id="signature" class="textarea" rows="4" [(ngModel)]="signatureInput" placeholder="Appended to new messages"></textarea>
              </div>

              <button class="btn btn-primary" (click)="saveProfile()" [disabled]="saving()" style="width: fit-content">Save profile</button>
            </div>
          </div>

          <div class="card">
            <div class="card-header"><div class="card-title">Change password</div></div>
            <div class="card-body stack">
              <div class="field">
                <label for="current">Current password</label>
                <input id="current" class="input" type="password" [(ngModel)]="currentPassword" autocomplete="current-password" />
              </div>
              <div class="field">
                <label for="next">New password</label>
                <input id="next" class="input" type="password" [(ngModel)]="newPassword" autocomplete="new-password" />
                <span class="subtle small">At least 12 characters with upper, lower, digit and symbol.</span>
              </div>
              <button class="btn btn-primary" (click)="changePassword()" [disabled]="saving()" style="width: fit-content">Update password</button>
              <p class="subtle small">All active sessions are signed out after a password change.</p>
            </div>
          </div>
        </div>

        <div class="stack">
          <div class="card">
            <div class="card-header">
              <div class="card-title">Two-factor authentication</div>
              <span class="badge" [class.badge-ok]="twoFactorEnabled()" [class.badge-warn]="!twoFactorEnabled()">
                {{ twoFactorEnabled() ? 'Enabled' : 'Disabled' }}
              </span>
            </div>
            <div class="card-body stack">
              @if (!twoFactorEnabled()) {
                @if (setup(); as setupData) {
                  <p class="small muted">Add this account to your authenticator app, then enter the generated code.</p>
                  <div class="field">
                    <label>Setup key</label>
                    <div class="row" style="gap: 8px">
                      <input class="input mono" [value]="setupData.sharedKey" readonly />
                      <button class="btn btn-sm" (click)="copy(setupData.sharedKey)">Copy</button>
                    </div>
                  </div>
                  <div class="field">
                    <label for="otp">Authenticator code</label>
                    <input id="otp" class="input mono" [(ngModel)]="totpCode" placeholder="000000" inputmode="numeric" />
                  </div>
                  <button class="btn btn-primary" (click)="enableTwoFactor()" style="width: fit-content">Enable 2FA</button>
                } @else {
                  <p class="small muted">Protect your account with a time-based one-time password (TOTP).</p>
                  <button class="btn" (click)="startTwoFactor()" style="width: fit-content">Set up 2FA</button>
                }
              } @else {
                <p class="small muted">Two-factor authentication is active. Enter a current code to disable it.</p>
                <div class="field">
                  <label for="otpDisable">Authenticator code</label>
                  <input id="otpDisable" class="input mono" [(ngModel)]="totpCode" placeholder="000000" inputmode="numeric" />
                </div>
                <button class="btn btn-danger" (click)="disableTwoFactor()" style="width: fit-content">Disable 2FA</button>
              }
            </div>
          </div>

          <div class="card">
            <div class="card-header"><div class="card-title">Your mailboxes</div></div>
            <div class="card-body stack" style="gap: 12px">
              @for (mailbox of mailboxes(); track mailbox.id) {
                <div>
                  <div class="row-between">
                    <div>
                      <div style="font-weight: 600">{{ mailbox.address }}</div>
                      <div class="subtle small">{{ mailbox.domainName }}</div>
                    </div>
                    <span class="badge" [class.badge-info]="mailbox.isShared">{{ mailbox.isShared ? 'Shared' : 'Personal' }}</span>
                  </div>
                  <div class="row-between small muted" style="margin-top: 6px">
                    <span>{{ mailbox.usedBytes | fileSize }} of {{ mailbox.quotaBytes | fileSize }}</span>
                    <span>{{ mailbox.quotaUsedPercent }}%</span>
                  </div>
                  <div class="meter" style="height: 6px; border-radius: 999px; background: var(--surface-3); overflow: hidden; margin-top: 4px">
                    <span style="display: block; height: 100%; background: linear-gradient(90deg, var(--brand-500), var(--violet-500))" [style.width.%]="mailbox.quotaUsedPercent"></span>
                  </div>
                </div>
              } @empty {
                <div class="empty" style="padding: 20px">No mailboxes assigned to your account yet.</div>
              }
            </div>
          </div>

          <div class="card">
            <div class="card-header"><div class="card-title">Sessions</div></div>
            <div class="card-body">
              <p class="small muted">Signing out revokes every refresh token issued to your account.</p>
              <button class="btn btn-danger" (click)="auth.logout()" style="width: fit-content">Sign out everywhere</button>
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
})
export class SettingsComponent implements OnInit {
  readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

  readonly displayName = () => this.auth.user()?.displayName ?? '';
  readonly email = () => this.auth.user()?.email ?? '';
  readonly roles = () => this.auth.user()?.roles ?? [];
  readonly mailboxes = this.auth.mailboxes;
  readonly twoFactorEnabled = signal(this.auth.user()?.twoFactorEnabled ?? false);
  readonly setup = signal<{ sharedKey: string; authenticatorUri: string } | null>(null);
  readonly saving = signal(false);

  displayNameInput = '';
  signatureInput = '';
  currentPassword = '';
  newPassword = '';
  totpCode = '';

  ngOnInit(): void {
    this.displayNameInput = this.auth.user()?.displayName ?? '';
    this.signatureInput = this.auth.user()?.signature ?? '';
  }

  saveProfile(): void {
    this.saving.set(true);
    this.api.updateProfile(this.displayNameInput, this.signatureInput).subscribe({
      next: (me) => {
        this.saving.set(false);
        this.auth.updateUser(me);
        this.toast.success('Profile updated');
      },
      error: (error) => {
        this.saving.set(false);
        this.toast.error(ApiService.toError(error).detail);
      },
    });
  }

  changePassword(): void {
    if (!this.currentPassword || !this.newPassword) {
      this.toast.error('Enter your current and new password');
      return;
    }

    this.saving.set(true);
    this.api.changePassword(this.currentPassword, this.newPassword).subscribe({
      next: () => {
        this.saving.set(false);
        this.currentPassword = '';
        this.newPassword = '';
        this.toast.success('Password updated - please sign in again');
        this.auth.logout();
      },
      error: (error) => {
        this.saving.set(false);
        this.toast.error(ApiService.toError(error).detail);
      },
    });
  }

  startTwoFactor(): void {
    this.api.setupTwoFactor().subscribe({
      next: (setup) => this.setup.set(setup),
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  enableTwoFactor(): void {
    this.api.enableTwoFactor(this.totpCode).subscribe({
      next: () => {
        this.twoFactorEnabled.set(true);
        this.setup.set(null);
        this.totpCode = '';
        this.toast.success('Two-factor authentication enabled');
      },
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  disableTwoFactor(): void {
    this.api.disableTwoFactor(this.totpCode).subscribe({
      next: () => {
        this.twoFactorEnabled.set(false);
        this.totpCode = '';
        this.toast.info('Two-factor authentication disabled');
      },
      error: (error) => this.toast.error(ApiService.toError(error).detail),
    });
  }

  copy(value: string): void {
    void navigator.clipboard?.writeText(value);
    this.toast.info('Copied');
  }
}
