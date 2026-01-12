import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, Inject } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertModule } from '@mintplayer/ng-bootstrap/alert';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BehaviorSubject } from 'rxjs';

@Component({
  selector: 'app-home',
  templateUrl: './home.component.html',
  imports: [CommonModule, BsButtonTypeDirective, BsAlertModule]
})
export class HomeComponent {
  constructor(private httpClient: HttpClient, @Inject('BASE_URL') private baseUrl: string) {}

  sendRequest() {
    this.httpClient.post(`${this.baseUrl}/WeatherForecast`, {}).subscribe({
      next: (response) => this.status$.next({ success: true, message: 'Request sent successfully' }),
      error: (error) => this.status$.next({ success: false, message: 'Error sending the request' }),
    });
  }

  colors = Color;
  status$ = new BehaviorSubject<StatusMessage | null>(null);
}

interface StatusMessage {
  success: boolean;
  message: string;
}
