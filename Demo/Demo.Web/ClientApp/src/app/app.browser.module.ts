import { NgModule } from "@angular/core";
import { HttpClientModule } from '@angular/common/http';
import { AppComponent } from './app.component';
import { AppModule } from './app.module';

@NgModule({
  imports: [
    AppModule,
    HttpClientModule,
  ],
  bootstrap: [AppComponent]
})
export class AppBrowserModule {
}
