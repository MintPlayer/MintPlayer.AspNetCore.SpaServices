import { ComponentFixture, TestBed } from '@angular/core/testing';

import { PersonListComponent } from './list.component';

describe('PersonListComponent', () => {
	let component: PersonListComponent;
	let fixture: ComponentFixture<PersonListComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [ PersonListComponent ]
    })
    .compileComponents();

    fixture = TestBed.createComponent(ListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
