import { ComponentFixture, TestBed } from '@angular/core/testing';

import { PersonShowComponent } from './show.component';

describe('PersonShowComponent', () => {
	let component: PersonShowComponent;
	let fixture: ComponentFixture<PersonShowComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
			declarations: [PersonShowComponent]
    })
    .compileComponents();

		fixture = TestBed.createComponent(PersonShowComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
