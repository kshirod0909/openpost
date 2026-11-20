import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ToastService } from './core/toast.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: `
    <router-outlet />

    <div class="toast-host">
      @for (toast of toasts(); track toast.id) {
        <div class="toast" [class.toast-ok]="toast.kind === 'ok'" [class.toast-error]="toast.kind === 'error'" [class.toast-info]="toast.kind === 'info'">
          <span class="grow">{{ toast.message }}</span>
          <button class="btn btn-ghost btn-icon btn-sm" (click)="dismiss(toast.id)" aria-label="Dismiss">&#10005;</button>
        </div>
      }
    </div>
  `,
})
export class AppComponent {
  private readonly toastService = inject(ToastService);
  readonly toasts = this.toastService.toasts;

  dismiss(id: number): void {
    this.toastService.dismiss(id);
  }
}
