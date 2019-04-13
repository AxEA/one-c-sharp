﻿using Microsoft.Practices.Prism.Commands;
using Microsoft.Practices.Prism.Interactivity.InteractionRequest;
using Microsoft.Practices.Prism.Mvvm;
using Microsoft.Practices.Prism.PubSubEvents;
using Microsoft.Practices.Prism.Regions;
using Microsoft.Practices.Unity;
using System;
using System.Windows.Input;
using Zhichkin.Shell;

namespace Zhichkin.Hermes.UI
{
    public class MainMenuViewModel : BindableBase
    {
        private const string CONST_ModuleDialogsTitle = "Hermes";

        private readonly IUnityContainer container;
        private readonly IRegionManager regionManager;
        private readonly IEventAggregator eventAggregator;

        public MainMenuViewModel(IUnityContainer container, IRegionManager regionManager, IEventAggregator eventAggregator)
        {
            if (container == null) throw new ArgumentNullException("container");
            if (regionManager == null) throw new ArgumentNullException("regionManager");
            if (eventAggregator == null) throw new ArgumentNullException("eventAggregator");
            this.container = container;
            this.regionManager = regionManager;
            this.eventAggregator = eventAggregator;
            this.AddNewQueryCommand = new DelegateCommand(this.AddNewQuery);
            this.AddNewSelectStatementCommand = new DelegateCommand(this.AddNewSelectStatement);
            this.AddTableToSelectStatementCommand = new DelegateCommand(this.AddTableToSelectStatement);
            this.ChangeFromOrientationCommand = new DelegateCommand(this.ChangeFromOrientation);
            this.BuildMetadataTreeCommand = new DelegateCommand(this.BuildMetadataTree);
        }
        private string GetErrorText(Exception ex)
        {
            string errorText = string.Empty;
            Exception error = ex;
            while (error != null)
            {
                errorText += (errorText == string.Empty) ? error.Message : Environment.NewLine + error.Message;
                error = error.InnerException;
            }
            return errorText;
        }
        public ICommand AddNewQueryCommand { get; private set; }
        public ICommand AddNewSelectStatementCommand { get; private set; }
        public ICommand AddTableToSelectStatementCommand { get; private set; }
        public ICommand ChangeFromOrientationCommand { get; private set; }
        public ICommand BuildMetadataTreeCommand { get; private set; }
        
        private void ErrorProneCommand()
        {
            try
            {
                AddNewQuery();
            }
            catch (Exception ex)
            {
                Z.Notify(new Notification { Title = CONST_ModuleDialogsTitle, Content = GetErrorText(ex) });
            }
        }

        private QueryExpression query;
        private void AddNewQuery()
        {
            //Z.Notify(new Notification { Title = CONST_ModuleDialogsTitle, Content = "Hello from Hermes!" });
            Z.ClearRightRegion(this.regionManager);
            IRegion rightRegion = this.regionManager.Regions[RegionNames.RightRegion];
            if (rightRegion == null) return;

            query = new QueryExpression();
            //query.QueryParameters.Add(new ParameterExpression(query) { Name = "Parameter0", Type = new EntityInfo(1) { Name = "GUID" } });
            //query.QueryParameters.Add(new ParameterExpression(query) { Name = "Parameter1", Type = new EntityInfo(2) { Name = "Int32" } });
            //query.QueryParameters.Add(new ParameterExpression(query) { Name = "Parameter2", Type = new EntityInfo(3) { Name = "String" } });
            //query.QueryParameters.Add(new ParameterExpression(query) { Name = "Parameter3", Type = new EntityInfo(4) { Name = "Boolean" } });

            SelectStatementViewModel select = new SelectStatementViewModel(null);
            query.QueryExpressions.Add(select);

            QueryView queryView = new QueryView(query);
            
            rightRegion.Add(queryView);
        }
        private void AddNewSelectStatement()
        {
            SelectStatementViewModel select = new SelectStatementViewModel(null);
            query.QueryExpressions.Add(select);
        }
        private void AddTableToSelectStatement()
        {
            if (query.QueryExpressions.Count == 0) return;
            SelectStatementViewModel select = query.QueryExpressions[0];
            select.Tables.Add(new TableExpressionViewModel(null) { Alias = "T1" });
        }
        private void ChangeFromOrientation()
        {
            if (query.QueryExpressions.Count == 0) return;
            SelectStatementViewModel select = query.QueryExpressions[0];
            select.IsFromVertical = !select.IsFromVertical;
        }

        private void BuildMetadataTree()
        {
            Z.ClearRightRegion(this.regionManager);
            IRegion rightRegion = this.regionManager.Regions[RegionNames.RightRegion];
            if (rightRegion == null) return;

            MetadataTreeViewModel model = new MetadataTreeViewModel();
            //this.eventAggregator.GetEvent<MetadataTreeViewItemSelected>().Subscribe(model.OnMetadataTreeViewItemSelected, true);
            MetadataTreeView view = new MetadataTreeView();
            view.DataContext = model;
            rightRegion.Add(view);
        }
    }
}
