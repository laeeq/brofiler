﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Threading;
using System.Windows.Media;
using System.ComponentModel;

namespace Profiler.Data
{
	public abstract class BaseTreeNode : INotifyPropertyChanged
	{
		private bool isExpanded;
		private bool isSelected;
		private Visibility visible;

		public BaseTreeNode Parent { get; private set; }
		public BaseTreeNode RootParent { get; private set; }
		public List<BaseTreeNode> Children { get; private set; }

		public double Duration { get; protected set; }
		public double ChildrenDuration { get; protected set; }
		public double SelfDuration { get { return Duration - ChildrenDuration; } }

		public abstract String Name { get; }

		public event PropertyChangedEventHandler PropertyChanged;
		private void Raise(string propertyName)
		{
			var tmp = PropertyChanged;
			if (tmp != null)
				tmp(this, new PropertyChangedEventArgs(propertyName));
		}

		public double Ratio
        {
            get
            {
                return Parent != null ? Duration / Parent.Duration : 1.0;
            }
        }

		public double TotalPercent { get { return RootParent != null ? (100.0 * Duration / RootParent.Duration) : 100.0; } }
		public double SelfPercent { get { return RootParent != null ? (100.0 * SelfDuration / RootParent.Duration) : (SelfDuration / Duration); } }

		public abstract String Path { get; }

		public bool IsExpanded
		{
			get 
			{
				return isExpanded;
			}
			set 
			{
				if (isExpanded != value)
				{
					isExpanded = value;
					Raise("IsExpanded");
				}
			}
		}

		public bool IsSelected
		{
			get
			{
				return isSelected;
			}
			set
			{
				if (isSelected != value)
				{
					isSelected = value;
					Raise("IsSelected");
				}
			}
		}


		public Visibility Visible
		{
			get
			{
				return visible;
			}
			set
			{
				if (visible != value)
				{
					visible = value;
					Raise("Visible");
				}
			}
		}
		

		public void ApplyFilterTerminal(FilterMode mode)
		{
			if (Duration < mode.TimeLimit)
			{
				Hide();
				return;
			}

			//if (Expanded == false && Visible == Visibility.Visible)
			//  return;

			IsExpanded = false;
			Visible = Visibility.Visible;

			foreach (var node in Children)
				node.ApplyFilterTerminal(mode);
		}

		public void Hide()
		{
			if (IsExpanded == false && Visible == Visibility.Collapsed)
				return;

			IsExpanded = false;
			Visible = Visibility.Collapsed;
		}

		public BaseTreeNode(BaseTreeNode root, double duration)
		{
			Visible = Visibility.Visible;

			RootParent = root;
			Children = new List<BaseTreeNode>();
			Duration = duration;
		}

		public void AddChild(BaseTreeNode node)
		{
			node.Parent = this;

			Children.Add(node);
			ChildrenDuration += node.Duration;
		}

		public abstract void ApplyFilter(HashSet<Object> roof, HashSet<Object> nodes, FilterMode mode);
		public abstract void CalculateRecursiveExcludeFlag(Dictionary<Object, int> parentCallStorage);

		public delegate bool TreeNodeDelegate(BaseTreeNode node, int level);
		public void ForEach(TreeNodeDelegate action, int level = 0)
		{
			if (!action(this, level))
				return;

			Children.ForEach(node => node.ForEach(action, level + 1));
		}
	}

	public abstract class TreeNode<TDescription> : BaseTreeNode
														  where TDescription : Description
	{
		public TDescription Description { get; private set; }

		public override String Path { get { return Description.Path.ToString(); } }
		public override String Name { get { return Description.Name; } }

		public bool ExcludeFromTotal { get; private set; }

		public TreeNode(TreeNode<TDescription> root, TDescription desc, double duration)
		  : base(root, duration)
		{
			Description = desc;
		}

		public override void ApplyFilter(HashSet<Object> roof, HashSet<Object> nodes, FilterMode mode)
		{
			if (Duration < mode.TimeLimit)
			{
				Hide();
			}
			else if (roof != null && roof.Contains(this))
			{
				Visible = Visibility.Visible;

				foreach (var node in Children)
					node.ApplyFilter(roof, nodes, mode);

				IsExpanded = true;
			}
			else if (nodes == null || nodes.Contains(this) || !mode.HideNotRelative)
			{
				Application.Current.Dispatcher.BeginInvoke(new Action(() =>
				{
					ApplyFilterTerminal(mode);
				}), System.Windows.Threading.DispatcherPriority.DataBind);
				return;
			}
			else
			{
				Hide();
			}
		}

		public double CalculateFilteredTime(HashSet<Object> filter)
		{
			if (filter.Contains(Description))
				return Duration;

			double sum = 0.0;

			foreach (var node in Children)
				sum += (node as TreeNode<TDescription>).CalculateFilteredTime(filter);

			return sum;
		}

		public override void CalculateRecursiveExcludeFlag(Dictionary<Object, int> parentCallStorage)
		{
			Object key = Description != null ? Description.GetSharedKey() : null;

			if (key == null)
			{
				foreach (var node in Children)
					node.CalculateRecursiveExcludeFlag(parentCallStorage);
			}
			else
			{
				int count = 0;
				if (parentCallStorage.TryGetValue(key, out count))
				{
					ExcludeFromTotal = count > 0;
					parentCallStorage[Description] = count + 1;
				}
				else
				{
					ExcludeFromTotal = false;
					parentCallStorage.Add(key, 1);
				}

				foreach (var node in Children)
					node.CalculateRecursiveExcludeFlag(parentCallStorage);

				parentCallStorage[key]--;
			}
		}
	}

	public class EventNode : TreeNode<EventDescription>
	{
		public Entry Entry { get; private set; }
		public Brush Brush { get { return Entry.Description.Brush;  } }

		public EventNode(EventNode root, Entry entry)
		  : base(root, entry.Description, entry.Duration)
		{
			Entry = entry;
		}
	}

	public class EventTree : EventNode
	{
		private int depth = 1;
		private EventFrame frame;
		public EventTree(EventFrame frame, List<Entry> entries) : base(null, new Entry(null, frame.Start, frame.Finish))
		{
			this.frame = frame;
			BuildTree(entries);
			CalculateRecursiveExcludeFlag(new Dictionary<Object, int>());
		}

		public int Depth
		{
			get { return depth - 1; }
		}

		private void BuildTree(List<Entry> entries)
		{
			if (entries.Count == 0)
				return;

			Stack<EventNode> curNodes = new Stack<EventNode>();
			curNodes.Push(this);
			depth = curNodes.Count;

			foreach (var entry in entries)
			{
				if (entry.Start == entry.Finish)
					continue;

				while (entry.Start >= curNodes.Peek().Entry.Finish)
				{
					curNodes.Pop();
				}

				EventNode node = new EventNode(this, entry);

				curNodes.Peek().AddChild(node);
				curNodes.Push(node);
				depth = Math.Max(depth, curNodes.Count);
			}

			while (curNodes.Count > 0)
			{
				curNodes.Pop();
			}
		}

		public void ForEachChild(TreeNodeDelegate action)
		{
			Children.ForEach(node => node.ForEach(action));
		}
	}
}
