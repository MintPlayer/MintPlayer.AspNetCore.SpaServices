import { Component, Inject } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { ActivatedRoute, Router } from '@angular/router';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.scss']
})
export class AppComponent {
  title = 'ClientApp';
  message = '';

  /**
   * npm install --save @ngx-translate/core @ngx-translate/http-loader
   * 
   */

  constructor(private translateService: TranslateService, private router: Router, private route: ActivatedRoute, @Inject('MESSAGE') message: string) {
    this.message = message;
    this.translateService.setDefaultLang('en');
    this.route.queryParamMap.subscribe((params) => {
      let lang = params.get('lang');
      console.log('language', lang);
      this.translateService.use(lang);
    });
  }

  useLanguage(language: string) {
    this.router.navigate([], {
      queryParams: {
        lang: language
      }
    });
  }
}
