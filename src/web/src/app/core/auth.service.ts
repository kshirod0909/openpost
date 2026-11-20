import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, finalize, map, of, shareReplay, tap, throwError } from 'rxjs';
import { Me, TokenResponse } from './models';

const REFRESH_KEY = 'openpost.refresh';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  readonly user = signal<Me | null>(null);
  readonly isAuthenticated = computed(() => this.user() !== null);
  readonly isAdmin = computed(() => {
    const roles = this.user()?.roles ?? [];
    return roles.includes('SystemAdmin') || roles.includes('DomainAdmin');
  });
  readonly isSystemAdmin = computed(() => (this.user()?.roles ?? []).includes('SystemAdmin'));
  readonly mailboxes = computed(() => this.user()?.mailboxes ?? []);

  /** Access token is kept in memory only; the refresh token is the only persisted secret. */
  private accessToken: string | null = null;
  private refreshInFlight: Observable<TokenResponse> | null = null;

  get token(): string | null {
    return this.accessToken;
  }

  login(email: string, password: string, totpCode?: string): Observable<TokenResponse> {
    return this.http
      .post<TokenResponse>('/api/v1/auth/login', { email, password, totpCode, rememberMe: true })
      .pipe(tap((response) => this.applySession(response)));
  }

  /** Restores the session from a stored refresh token at application start. */
  restore(): Observable<boolean> {
    const stored = localStorage.getItem(REFRESH_KEY);
    if (!stored) return of(false);

    return this.refresh().pipe(
      map(() => true),
      catchError(() => {
        this.clear();
        return of(false);
      }),
    );
  }

  refresh(): Observable<TokenResponse> {
    if (this.refreshInFlight) return this.refreshInFlight;

    const stored = localStorage.getItem(REFRESH_KEY);
    if (!stored) return throwError(() => ({ status: 401, title: 'Not signed in', detail: 'No session' }));

    this.refreshInFlight = this.http.post<TokenResponse>('/api/v1/auth/refresh', { refreshToken: stored }).pipe(
      tap((response) => this.applySession(response)),
      finalize(() => (this.refreshInFlight = null)),
      shareReplay(1),
    );

    return this.refreshInFlight;
  }

  updateUser(user: Me): void {
    this.user.set(user);
  }

  logout(redirect = true): void {
    this.http.post('/api/v1/auth/logout', {}).subscribe({ error: () => undefined });
    this.clear();
    if (redirect) void this.router.navigate(['/login']);
  }

  clear(): void {
    this.accessToken = null;
    this.user.set(null);
    localStorage.removeItem(REFRESH_KEY);
  }

  private applySession(response: TokenResponse): void {
    this.accessToken = response.accessToken;
    this.user.set(response.user);
    localStorage.setItem(REFRESH_KEY, response.refreshToken);
  }
}
