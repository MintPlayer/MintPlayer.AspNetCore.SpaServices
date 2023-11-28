import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterOutlet, Router, ActivatedRoute } from '@angular/router';
import { TranslateService } from '@ngx-translate/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, RouterOutlet],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss'
})
export class AppComponent {
  title = 'ClientApp';
  message = 'test';

  constructor(private translateService: TranslateService, private router: Router, private route: ActivatedRoute, @Inject('MESSAGE') message: string) {
    this.message = message;
    this.translateService.setDefaultLang('en');
    this.route.queryParamMap
      .pipe(takeUntilDestroyed())
      .subscribe((params: any) => {
        const lang = params.get('lang');
        if (lang) {
          this.translateService.use(lang);
        }
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
