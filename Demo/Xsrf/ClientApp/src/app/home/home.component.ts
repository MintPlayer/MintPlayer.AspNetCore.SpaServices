import { HttpClient } from '@angular/common/http';
import { Component, Inject } from '@angular/core';

@Component({
  selector: 'app-home',
  templateUrl: './home.component.html',
})
export class HomeComponent {
  constructor(private httpClient: HttpClient, @Inject('BASE_URL') private baseUrl: string) {
  }

  sendRequest() {
    this.httpClient.post(`//localhost:44343/WeatherForecast`, {}).subscribe((response) => {
      console.log('Request sent successfully');
    }, (error) => {
      console.error('Error sending the request');
    });
  }
}
