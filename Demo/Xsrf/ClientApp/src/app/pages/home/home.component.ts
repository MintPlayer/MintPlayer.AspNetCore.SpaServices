import { HttpClient } from '@angular/common/http';
import { Component, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertModule } from '@mintplayer/ng-bootstrap/alert';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BASE_URL_TOKEN } from '../../tokens';

interface StatusMessage {
  success: boolean;
  message: string;
}

@Component({
  selector: 'app-home',
  templateUrl: './home.component.html',
  imports: [BsButtonTypeDirective, BsAlertModule],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class HomeComponent {
  private readonly httpClient = inject(HttpClient);
  private readonly baseUrl = inject(BASE_URL_TOKEN);

  colors = Color;
  status = signal<StatusMessage | null>(null);

  sendRequest() {
    this.httpClient.post(`${this.baseUrl}/WeatherForecast`, {}).subscribe({
      next: () => this.status.set({ success: true, message: 'Request sent successfully' }),
      error: () => this.status.set({ success: false, message: 'Error sending the request' }),
    });
  }
}
