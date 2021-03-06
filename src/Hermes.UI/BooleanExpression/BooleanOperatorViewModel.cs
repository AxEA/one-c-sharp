﻿using Microsoft.Practices.Prism.Commands;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Zhichkin.Hermes.Model;

namespace Zhichkin.Hermes.UI
{
    public class BooleanOperatorViewModel : BooleanFunctionViewModel
    {
        public BooleanOperatorViewModel(HermesViewModel parent, BooleanOperator model) : base(parent, model)
        {
            this.IntitializeViewModel(model);

            this.AddComparisonOperatorCommand = new DelegateCommand(this.AddComparisonOperator);
            this.AddInnerBooleanOperatorCommand = new DelegateCommand(this.AddInnerBooleanOperator);
            this.AddOuterBooleanOperatorCommand = new DelegateCommand(this.AddOuterBooleanOperator);
            this.RemoveBooleanOperatorCommand = new DelegateCommand(this.RemoveBooleanOperator);
        }
        private void IntitializeViewModel(BooleanOperator model)
        {
            if (model == null) return;
            if (this.Operands == null) { this.Operands = new ObservableCollection<BooleanFunctionViewModel>(); }

            foreach (BooleanFunction operand in model.Operands)
            {
                if (operand is BooleanOperator)
                {
                    this.Operands.Add(new BooleanOperatorViewModel(this, (BooleanOperator)operand));
                }
                else if (operand is ComparisonOperator)
                {
                    this.Operands.Add(new ComparisonOperatorViewModel(this, (ComparisonOperator)operand));
                }
            }
        }

        public ICommand AddComparisonOperatorCommand { get; private set; }
        public ICommand AddInnerBooleanOperatorCommand { get; private set; }
        public ICommand AddOuterBooleanOperatorCommand { get; private set; }
        public ICommand RemoveBooleanOperatorCommand { get; private set; }

        public ObservableCollection<BooleanFunctionViewModel> Operands { get; set; }
        public List<string> BooleanOperators { get { return BooleanFunction.BooleanOperators; } }

        public void RemoveChildOperator(BooleanFunctionViewModel child)
        {
            this.Operands.Remove(child);
        }
        
        private void AddComparisonOperator()
        {
            BooleanOperator model = this.Model as BooleanOperator;
            if (model == null) return;
            if (!model.IsLeaf) return;

            ComparisonOperator operand = new ComparisonOperator(model);
            model.AddChild(operand);
            ComparisonOperatorViewModel viewModel = new ComparisonOperatorViewModel(this, operand);
            this.Operands.Add(viewModel);
        }
        private void AddInnerBooleanOperator()
        {
            BooleanOperator model = this.Model as BooleanOperator;
            if (model == null) return;

            if (model.IsLeaf)
            {
                AddOuterBooleanOperator();
            }
            else
            {
                BooleanOperator operand = new BooleanOperator(model) { Name = BooleanFunction.OR };
                model.AddChild(operand);
                ComparisonOperator child = new ComparisonOperator(operand);
                operand.AddChild(child);
                BooleanOperatorViewModel viewModel = new BooleanOperatorViewModel(this, operand);
                ComparisonOperatorViewModel childVM = new ComparisonOperatorViewModel(viewModel, child);
                if (viewModel.Operands == null)
                {
                    viewModel.Operands = new ObservableCollection<BooleanFunctionViewModel>() { childVM };
                }
                else
                {
                    viewModel.Operands.Add(childVM);
                }
                if (this.Operands == null)
                {
                    this.Operands = new ObservableCollection<BooleanFunctionViewModel>() { viewModel };
                }
                else
                {
                    this.Operands.Add(viewModel);
                }
            }
        }
        private void AddOuterBooleanOperator()
        {
            BooleanOperator model = this.Model as BooleanOperator;
            if (model == null) return;

            // 0. Remember the parent of this node
            HermesModel consumer = model.Consumer;
            HermesViewModel parentVM = this.Parent;
            int index_to_replace = -1;
            if (consumer is BooleanOperator)
            {
                index_to_replace = ((BooleanOperator)consumer).Operands.IndexOf(model);
                if (index_to_replace == -1)
                {
                    throw new ArgumentOutOfRangeException("Model is broken!");
                }
            }

            // 1. Create new node and it's VM which will substitute this current node
            BooleanOperator substitute = new BooleanOperator(consumer) { Name = BooleanFunction.OR };
            substitute.AddChild(model);
            BooleanOperatorViewModel substituteVM = new BooleanOperatorViewModel(parentVM, substitute);
            this.Parent = substituteVM;

            // 2. Create new child and it's VM consumed by substitute
            BooleanOperator child = new BooleanOperator(substitute);
            substitute.AddChild(child);
            BooleanOperatorViewModel childVM = new BooleanOperatorViewModel(substituteVM, child);

            // 3. Add new comparison operator and it's VM to new born child
            ComparisonOperator gift = new ComparisonOperator(child);
            child.AddChild(gift);
            ComparisonOperatorViewModel giftVM = new ComparisonOperatorViewModel(childVM, gift);
            childVM.Operands = new ObservableCollection<BooleanFunctionViewModel>() { giftVM };

            // 4. Fill substitute VM with operands
            substituteVM.Operands = new ObservableCollection<BooleanFunctionViewModel>()
            {
                this,
                childVM
            };

            // 5. Substitute this current node at parent VM and it's model
            if (consumer is BooleanOperator)
            {
                ((BooleanOperator)consumer).Operands.RemoveAt(index_to_replace);
                ((BooleanOperator)consumer).Operands.Insert(index_to_replace, substitute);
                index_to_replace = ((BooleanOperatorViewModel)parentVM).Operands.IndexOf(this);
                if (index_to_replace > -1)
                {
                    ((BooleanOperatorViewModel)parentVM).Operands.RemoveAt(index_to_replace);
                    ((BooleanOperatorViewModel)parentVM).Operands.Insert(index_to_replace, substituteVM);
                }
            }
            else if (parentVM is BooleanExpressionViewModel)
            {
                ((BooleanExpressionViewModel)parentVM).SetBooleanExpression(substituteVM);
            }
        }
        private void RemoveBooleanOperator()
        {
            BooleanOperator model = this.Model as BooleanOperator;
            if (model == null) return;

            if (model.IsRoot)
            {
                if (this.Parent is BooleanExpressionViewModel)
                {
                    ((BooleanExpressionViewModel)this.Parent).ClearBooleanExpression();
                }
            }
            else
            {
                BooleanOperatorViewModel parent = this.Parent as BooleanOperatorViewModel;
                if (parent == null) return;

                BooleanOperator consumer = model.Consumer as BooleanOperator;
                if (consumer == null) return;

                if (consumer.Operands.Count == 0) return;

                consumer.Operands.Remove(model);
                parent.RemoveChildOperator(this);
                
                if (parent.Operands.Count == 1)
                {
                    BooleanFunctionViewModel orphan = parent.Operands[0];

                    if (parent.Parent is BooleanExpressionViewModel)
                    {
                        orphan.Name = parent.Name;
                        ((BooleanExpressionViewModel)parent.Parent).SetBooleanExpression(orphan);
                    }
                    else if (parent.Parent is BooleanOperatorViewModel)
                    {
                        orphan.Parent = parent.Parent;
                        orphan.Model.Consumer = parent.Parent.Model;
                        
                        int index_to_replace = ((BooleanOperator)consumer.Consumer).Operands.IndexOf(consumer);
                        ((BooleanOperator)consumer.Consumer).Operands.RemoveAt(index_to_replace);
                        ((BooleanOperator)consumer.Consumer).Operands.Insert(index_to_replace, (BooleanOperator)orphan.Model);

                        index_to_replace = ((BooleanOperatorViewModel)parent.Parent).Operands.IndexOf(parent);
                        ((BooleanOperatorViewModel)parent.Parent).Operands.RemoveAt(index_to_replace);
                        ((BooleanOperatorViewModel)parent.Parent).Operands.Insert(index_to_replace, orphan);
                    }
                }
            }
        }
    }
}