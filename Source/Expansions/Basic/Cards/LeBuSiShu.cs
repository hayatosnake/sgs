﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

using Sanguosha.Core.UI;
using Sanguosha.Core.Skills;
using Sanguosha.Core.Players;
using Sanguosha.Core.Games;
using Sanguosha.Core.Triggers;
using Sanguosha.Core.Exceptions;
using Sanguosha.Core.Cards;

namespace Sanguosha.Expansions.Basic.Cards
{
    
    public class LeBuSiShu : DelayedTool
    {
        public override void Activate(Player p, Card c)
        {
            while (true)
            {
                GameEventArgs args = new GameEventArgs();
                args.Source = null;
                args.Targets = new List<Player>() { p };
                args.Card = c;
                args.ReadonlyCard = new ReadOnlyCard(c);
                try
                {
                    Game.CurrentGame.Emit(GameEvent.CardUsageBeforeEffected, args);
                }
                catch (TriggerResultException e)
                {
                    Trace.Assert(e.Status == TriggerResult.End);
                    break;
                }
                ReadOnlyCard result = Game.CurrentGame.Judge(p, null, c, (judgeResultCard) => { return judgeResultCard.Suit != SuitType.Heart; });
                if (result.Suit != SuitType.Heart)
                {
                    Game.CurrentGame.PhasesSkipped.Add(TurnPhase.Play);
                }
                break;
            }
            CardsMovement move = new CardsMovement();
            move.Cards = new List<Card>();
            move.Cards.Add(c);
            move.To = new DeckPlace(null, DeckType.Discard);
            move.Helper.IsFakedMove = true;
            Game.CurrentGame.MoveCards(move, false, Core.Utils.GameDelays.None);
        }

        protected override void Process(Player source, Player dest, ICard card, ReadOnlyCard readonlyCard, GameEventArgs inResponseTo)
        {
            throw new NotImplementedException();
        }

        public override void Process(GameEventArgs handlerArgs)
        {
            var source = handlerArgs.Source;
            var dests = handlerArgs.Targets;
            var readonlyCard = handlerArgs.ReadonlyCard;
            var inResponseTo = handlerArgs.InResponseTo;
            var card = handlerArgs.Card;
            Trace.Assert(dests.Count == 1);
            AttachTo(source, dests[0], card);
        }

        public override VerifierResult Verify(Player source, ICard card, List<Player> targets, bool isLooseVerify)
        {
            if (targets != null && targets.Count != 0 &&
                (targets.Count > 1 || DelayedToolConflicting(targets[0]) || targets[0] == source))
            {
                return VerifierResult.Fail;
            }
            if (targets == null || targets.Count == 0)
            {
                return VerifierResult.Partial;
            }
            return VerifierResult.Success;
        }
    }
}
