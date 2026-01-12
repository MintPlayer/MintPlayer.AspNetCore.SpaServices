import { Component, Inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-fetch-data',
  templateUrl: './fetch-data.component.html',
  imports: [CommonModule]
})
export class FetchDataComponent {
  public forecasts: WeatherForecast[] = [];

  constructor(http: HttpClient, @Inject('BASE_URL') baseUrl: string) {
    http.get<WeatherForecast[]>(`${baseUrl}/WeatherForecast`).subscribe({
      next: (result) => this.forecasts = result,
      error: (error) => console.error(error)
    });
  }
}

interface WeatherForecast {
  date: string;
  temperatureC: number;
  temperatureF: number;
  summary: string;
}
