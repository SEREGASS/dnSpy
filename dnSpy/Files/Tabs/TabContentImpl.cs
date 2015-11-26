﻿/*
    Copyright (C) 2014-2015 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using dnSpy.Contracts.Files.Tabs;
using dnSpy.Contracts.Tabs;
using dnSpy.Shared.UI.MVVM;

namespace dnSpy.Files.Tabs {
	sealed class TabContentImpl : ViewModelBase, ITabContent, IFileTab {
		readonly TabHistory tabHistory;

		public IFileTabManager FileTabManager {
			get { return fileTabManager; }
		}
		readonly FileTabManager fileTabManager;

		public bool IsActiveTab {
			get { return FileTabManager.ActiveTab == this; }
		}

		public IFileTabContent Content {
			get { return tabHistory.Current; }
			private set {
				bool saveCurrent = !(tabHistory.Current is NullFileTabContent);
				tabHistory.SetCurrent(value, saveCurrent);
			}
		}

		public IFileTabUIContext UIContext {
			get { return uiContext; }
			private set {
				uiContextVersion++;
				var newValue = value;
				Debug.Assert(newValue != null);
				if (newValue == null)
					newValue = new NullFileTabUIContext();
				if (uiContext != newValue) {
					uiContext.OnHide();
					elementScaler.InstallScale(newValue.ScaleElement);
					newValue.OnShow();
					uiContext = newValue;
					UIObject = uiContext.UIObject;
				}
			}
		}
		IFileTabUIContext uiContext;
		int uiContextVersion;

		public string Title {
			get { return title; }
			set {
				if (title != value) {
					title = value;
					OnPropertyChanged("Title");
				}
			}
		}
		string title;

		public object ToolTip {
			get { return toolTip; }
			set {
				if (toolTip != value) {
					toolTip = value;
					OnPropertyChanged("ToolTip");
				}
			}
		}
		object toolTip;

		public object UIObject {
			get { return uiObject; }
			set {
				if (uiObject != value) {
					uiObject = value;
					OnPropertyChanged("UIObject");
				}
			}
		}
		object uiObject;

		UIElement ITabContent.FocusedElement {
			get {
				if (UIContext != null)
					return UIContext.FocusedElement;
				return null;
			}
		}

		readonly IFileTabUIContextLocator fileTabUIContextLocator;
		readonly Lazy<IReferenceFileTabContentCreator, IReferenceFileTabContentCreatorMetadata>[] refFactories;
		readonly TabElementScaler elementScaler;

		public TabContentImpl(FileTabManager fileTabManager, IFileTabUIContextLocator fileTabUIContextLocator, Lazy<IReferenceFileTabContentCreator, IReferenceFileTabContentCreatorMetadata>[] refFactories) {
			this.elementScaler = new TabElementScaler();
			this.tabHistory = new TabHistory();
			this.tabHistory.SetCurrent(new NullFileTabContent(), false);
			this.fileTabManager = fileTabManager;
			this.fileTabUIContextLocator = fileTabUIContextLocator;
			this.refFactories = refFactories;
			this.uiContext = new NullFileTabUIContext();
			this.uiObject = this.uiContext.UIObject;
		}

		void ITabContent.OnVisibilityChanged(TabContentVisibilityEvent visEvent) {
			if (visEvent == TabContentVisibilityEvent.Removed) {
				var id = fileTabUIContextLocator as IDisposable;
				Debug.Assert(id != null);
				if (id != null)
					id.Dispose();
			}
		}

		public void FollowReference(object @ref, IFileTabContent sourceContent) {
			var result = TryCreateContentFromReference(@ref, sourceContent);
			if (result != null)
				Show(result.FileTabContent, result.SerializedUI, result.OnShownHandler);
		}

		FileTabReferenceResult TryCreateContentFromReference(object @ref, IFileTabContent sourceContent) {
			foreach (var f in refFactories) {
				var c = f.Value.Create(FileTabManager, sourceContent, @ref);
				if (c != null)
					return c;
			}
			return null;
		}

		public void Show(IFileTabContent tabContent, object serializedUI, Action<ShowTabContentEventArgs> onShown) {
			if (tabContent == null)
				throw new ArgumentNullException();
			Debug.Assert(tabContent.FileTab == null || tabContent.FileTab == this);
			HideCurrentContent();
			Content = tabContent;
			ShowInternal(tabContent, serializedUI, onShown);
		}

		void HideCurrentContent() {
			CancelAsyncWorker();
			if (Content != null)
				Content.OnHide();
		}

		void ShowInternal(IFileTabContent tabContent, object serializedUI, Action<ShowTabContentEventArgs> onShownHandler) {
			Debug.Assert(asyncWorkerContext == null);
			var oldUIContext = UIContext;
			UIContext = tabContent.CreateUIContext(fileTabUIContextLocator);
			var cachedUIContext = UIContext;
			Debug.Assert(cachedUIContext.FileTab == null || cachedUIContext.FileTab == this);
			cachedUIContext.FileTab = this;
			Debug.Assert(cachedUIContext.FileTab == this);
			Debug.Assert(tabContent.FileTab == null || tabContent.FileTab == this);
			tabContent.FileTab = this;
			Debug.Assert(tabContent.FileTab == this);

			UpdateTitleAndToolTip();
			var userData = tabContent.OnShow(cachedUIContext);
			bool asyncShow = false;
			var asyncTabContent = tabContent as IAsyncFileTabContent;
			if (asyncTabContent != null) {
				if (asyncTabContent.CanStartAsyncWorker(cachedUIContext, userData)) {
					asyncShow = true;
					var ctx = new AsyncWorkerContext();
					asyncWorkerContext = ctx;
					Task.Factory.StartNew(() => {
						asyncTabContent.AsyncWorker(cachedUIContext, userData, ctx.CancellationTokenSource);
					}, ctx.CancellationTokenSource.Token)
					.ContinueWith(t => {
						bool canShowAsyncOutput = ctx == asyncWorkerContext &&
												cachedUIContext.FileTab == this &&
												UIContext == cachedUIContext;
						if (asyncWorkerContext == ctx)
							asyncWorkerContext = null;
						ctx.Dispose();
						asyncTabContent.EndAsyncShow(cachedUIContext, userData, new AsyncShowResult(t, canShowAsyncOutput));
						bool success = !t.IsFaulted && !t.IsCanceled;
						OnShown(serializedUI, onShownHandler, success);
					}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
				}
				else
					asyncTabContent.EndAsyncShow(cachedUIContext, userData, new AsyncShowResult());
			}
			if (!asyncShow)
				OnShown(serializedUI, onShownHandler, true);
			fileTabManager.OnNewTabContentShown(this);
		}

		sealed class AsyncWorkerContext : IDisposable {
			public readonly CancellationTokenSource CancellationTokenSource;

			public AsyncWorkerContext() {
				this.CancellationTokenSource = new CancellationTokenSource();
			}

			public void Dispose() {
				this.CancellationTokenSource.Dispose();
			}
		}
		AsyncWorkerContext asyncWorkerContext;

		void CancelAsyncWorker() {
			if (asyncWorkerContext == null)
				return;
			asyncWorkerContext.CancellationTokenSource.Cancel();
			asyncWorkerContext = null;
		}

		void OnShown(object serializedUI, Action<ShowTabContentEventArgs> onShownHandler, bool success) {
			if (serializedUI != null)
				Deserialize(serializedUI);
			if (onShownHandler != null)
				onShownHandler(new ShowTabContentEventArgs(success));
		}

		void Deserialize(object serializedUI) {
			if (serializedUI == null)
				return;
			UIContext.Deserialize(serializedUI);
			var uiel = UIContext.FocusedElement as UIElement ?? UIContext.UIObject as UIElement;
			if (uiel.IsVisible)
				return;
			int uiContextVersionTmp = uiContextVersion;
			new OnVisibleHelper(uiel, () => {
				if (uiContextVersionTmp == uiContextVersion)
					UIContext.Deserialize(serializedUI);
			});
		}

		sealed class OnVisibleHelper {
			readonly UIElement uiel;
			readonly Action exec;

			public OnVisibleHelper(UIElement uiel, Action exec) {
				this.uiel = uiel;
				this.exec = exec;
				this.uiel.IsVisibleChanged += UIElement_IsVisibleChanged;
			}

			void UIElement_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) {
				this.uiel.IsVisibleChanged -= UIElement_IsVisibleChanged;
				exec();
			}
		}

		internal void UpdateTitleAndToolTip() {
			Title = Content.Title;
			ToolTip = Content.ToolTip;
		}

		public bool CanNavigateBackward {
			get { return tabHistory.CanNavigateBackward; }
		}

		public bool CanNavigateForward {
			get { return tabHistory.CanNavigateForward; }
		}

		public void NavigateBackward() {
			if (!CanNavigateBackward)
				return;
			HideCurrentContent();
			var serialized = tabHistory.NavigateBackward();
			ShowInternal(tabHistory.Current, serialized, null);
		}

		public void NavigateForward() {
			if (!CanNavigateForward)
				return;
			HideCurrentContent();
			var serialized = tabHistory.NavigateForward();
			ShowInternal(tabHistory.Current, serialized, null);
		}

		public void Refresh() {
			// Pretend it gets hidden and then shown again. Will also cancel any async output threads
			HideCurrentContent();
			ShowInternal(Content, UIContext.Serialize(), null);
		}

		public void SetFocus() {
			if (IsActiveTab)
				FileTabManager.SetFocus(this);
		}

		public void OnSelected() {
			Content.OnSelected();
		}

		public void OnUnselected() {
			Content.OnUnselected();
		}
	}
}
