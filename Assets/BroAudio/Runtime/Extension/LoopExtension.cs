using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Ami.Extension
{
	public static class LoopExtension
	{
		public enum Statement
		{
			Continue,
			Break,
		}

		public const int MaxIterationTimes = 10000;

		public static void Loop(Func<Statement> method, bool showErrorWhenInfiniteLoopOccurs = true)
		{
			// �۷���While(true);
			Predicate<object> predicate = (obj) => true;

			MainLoopLogic(predicate,method,showErrorWhenInfiniteLoopOccurs);
		}

		public static void While(Predicate<object> predicate, Func<Statement> method, bool showErrorWhenInfiniteLoopOccurs = true)
		{
			MainLoopLogic(predicate, method, showErrorWhenInfiniteLoopOccurs);
		}

		private static void MainLoopLogic(Predicate<object> predicate, Func<Statement> method, bool showErrorWhenInfiniteLoopOccurs)
		{
			if (method == null)
			{
				Debug.LogError(Ami.BroAudio.Utility.LogTitle + "Method is null!");
				return;
			}
			Statement statement;
			for (int i = 0; i < MaxIterationTimes; i++)
			{
				if (showErrorWhenInfiniteLoopOccurs && i == MaxIterationTimes - 1)
				{
					Debug.LogError(Ami.BroAudio.Utility.LogTitle + "There is an infinite loop!");
				}

				if (!predicate.Invoke(null))
				{
					return;
				}

				statement = method.Invoke();
				if (statement == Statement.Continue)
				{
					continue;
				}
				else if (statement == Statement.Break)
				{
					break;
				}
			}
		}

	}

}