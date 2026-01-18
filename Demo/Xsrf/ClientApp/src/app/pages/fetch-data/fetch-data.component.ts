import { Component, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { BASE_URL_TOKEN } from '../../tokens';

interface WeatherForecast {
  date: string;
  temperatureC: number;
  temperatureF: number;
  summary: string;
}

@Component({
  selector: 'app-fetch-data',
  templateUrl: './fetch-data.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class FetchDataComponent {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(BASE_URL_TOKEN);

  forecasts = signal<WeatherForecast[]>([]);

  constructor() {
    this.http.get<WeatherForecast[]>(`${this.baseUrl}/WeatherForecast`).subscribe({
      next: (result) => this.forecasts.set(result),
      error: (error) => console.error(error)
    });
  }
}
