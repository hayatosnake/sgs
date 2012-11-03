﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

using Sanguosha.Core.Triggers;
using Sanguosha.Core.Players;
using Sanguosha.Core.Cards;
using Sanguosha.Core.UI;
using Sanguosha.Core.Skills;
using Sanguosha.Core.Exceptions;
using Sanguosha.Core.Heroes;
using System.Threading;

namespace Sanguosha.Core.Games
{
    public class RoleGame : Game
    {
        public int NumberOfRebels { get; set; }
        public int NumberOfDefectors { get; set; }

        public class PlayerActionTrigger : Trigger
        {
            private class PlayerActionStageVerifier : CardUsageVerifier
            {
                public override UiHelper Helper { get { return new UiHelper() { IsActionStage = true }; } }
                public override VerifierResult FastVerify(Player source, ISkill skill, List<Card> cards, List<Player> players)
                {
                    if (!Game.CurrentGame.AllAlive(players))
                    {
                        return VerifierResult.Fail;
                    }
                    if ((cards == null || cards.Count == 0) && skill == null)
                    {
                        return VerifierResult.Fail;
                    }
                    if (skill is ActiveSkill)
                    {
                        if (Game.CurrentGame.CurrentPlayer.Hero.Skills.IndexOf(skill) < 0)
                        {
                            return VerifierResult.Fail;
                        }
                        GameEventArgs arg = new GameEventArgs();
                        arg.Source = Game.CurrentGame.CurrentPlayer;
                        arg.Targets = players;
                        arg.Cards = cards;
                        return ((ActiveSkill)skill).Validate(arg);
                    }
                    else if (skill is CardTransformSkill)
                    {
                        CardTransformSkill s = (CardTransformSkill)skill;
                        CompositeCard result;
                        VerifierResult ret = s.TryTransform(cards, null, out result);
                        if (ret == VerifierResult.Success)
                        {
                            return result.Type.Verify(Game.CurrentGame.CurrentPlayer, skill, cards, players);
                        }
                        if (ret == VerifierResult.Partial && players != null && players.Count != 0)
                        {
                            return VerifierResult.Fail;
                        }
                        return ret;
                    }
                    else if (skill != null)
                    {
                        return VerifierResult.Fail;
                    }
                    return cards[0].Type.Verify(Game.CurrentGame.CurrentPlayer, skill, cards, players);
                }


                public override IList<CardHandler> AcceptableCardType
                {
                    get { return null; }
                }
            }

            public override void Run(GameEvent gameEvent, GameEventArgs eventArgs)
            {
                Player currentPlayer = eventArgs.Game.CurrentPlayer;
                Trace.TraceInformation("Player {0} action.", currentPlayer.Id);
                while (true)
                {
                    Trace.Assert(Game.CurrentGame.UiProxies.ContainsKey(currentPlayer));
                    IUiProxy proxy = Game.CurrentGame.UiProxies[currentPlayer];
                    ISkill skill;
                    List<Card> cards;
                    List<Player> players;
                    PlayerActionStageVerifier v = new PlayerActionStageVerifier();
                    if (!proxy.AskForCardUsage(new Prompt(Prompt.PlayingPhasePrompt), v, out skill, out cards, out players))
                    {
                        break;
                    }
                    if (skill != null)
                    {
                        if (skill is ActiveSkill)
                        {
                            GameEventArgs arg = new GameEventArgs();
                            arg.Source = Game.CurrentGame.CurrentPlayer;
                            arg.Targets = players;
                            arg.Cards = cards;
                            ((ActiveSkill)skill).Commit(arg);
                            continue;
                        }
                        CompositeCard c;
                        CardTransformSkill s = (CardTransformSkill)skill;
                        VerifierResult r = s.TryTransform(cards, null, out c);
                        Trace.TraceInformation("Player used {0}", c.Type);
                    }
                    else
                    {
                        Trace.Assert(cards[0] != null && cards.Count == 1);
                        Trace.TraceInformation("Player used {0}", cards[0].Type);
                    }
                    try
                    {
                        Game.CurrentGame.Emit(GameEvent.CommitActionToTargets, new Triggers.GameEventArgs() { Skill = skill, Source = Game.CurrentGame.CurrentPlayer, Targets = players, Cards = cards });
                    }
                    catch (TriggerResultException)
                    {
                    }
                }
            }
        }

        public class PlayerDealStageTrigger : Trigger
        {
            public override void Run(GameEvent gameEvent, GameEventArgs eventArgs)
            {
                Player currentPlayer = eventArgs.Game.CurrentPlayer;
                Trace.TraceInformation("Player {0} deal.", currentPlayer.Id);
                Game.CurrentGame.DrawCards(currentPlayer, 2);
            }
        }

        public class PlayerDiscardStageTrigger : Trigger
        {
            public override void Run(GameEvent gameEvent, GameEventArgs eventArgs)
            {
                Player currentPlayer = eventArgs.Game.CurrentPlayer;
                Trace.TraceInformation("Player {0} discard.", currentPlayer.Id);
                int cannotBeDiscarded = 0;
                // Have we finished discarding everything?
                // We finish if 
                //      玩家手牌数 小于等于 玩家体力值
                //  或者玩家手牌数 小于等于 不可弃的牌的数目
                while (true)
                {
                    int handCardCount = Game.CurrentGame.Decks[currentPlayer, DeckType.Hand].Count; // 玩家手牌数                    
                    int cardKept = Math.Max(cannotBeDiscarded, currentPlayer.Health);
                    if (handCardCount <= cardKept)
                    {
                        break;
                    }
                    Trace.Assert(Game.CurrentGame.UiProxies.ContainsKey(currentPlayer));
                    IUiProxy proxy = Game.CurrentGame.UiProxies[currentPlayer];
                    ISkill skill;
                    List<Card> cards;
                    List<Player> players;
                    PlayerDiscardStageVerifier v = new PlayerDiscardStageVerifier();
                    cannotBeDiscarded = 0;
                    foreach (Card c in Game.CurrentGame.Decks[currentPlayer, DeckType.Hand])
                    {
                        if (!Game.CurrentGame.PlayerCanDiscardCard(currentPlayer, c))
                        {
                            cannotBeDiscarded++;
                        }
                    }
                    //如果玩家体力 小于 不可弃的牌数 则 摊牌
                    bool status = currentPlayer.Health >= cannotBeDiscarded;
                    Game.CurrentGame.SyncConfirmationStatus(ref status);
                    if (!status)
                    {
                        Game.CurrentGame.SyncCardsAll(Game.CurrentGame.Decks[currentPlayer, DeckType.Hand]);
                    }

                    int promptCount = handCardCount - currentPlayer.Health;
                    if (!proxy.AskForCardUsage(new Prompt(Prompt.DiscardPhasePrompt, promptCount),
                                               v, out skill, out cards, out players))
                    {
                        //玩家没有回应(default)
                        //如果玩家有不可弃掉的牌(这个只有服务器知道） 则通知所有客户端该玩家手牌
                        status = (cannotBeDiscarded == 0);
                        Game.CurrentGame.SyncConfirmationStatus(ref status);
                        if (!status)
                        {
                            Game.CurrentGame.SyncCardsAll(Game.CurrentGame.Decks[currentPlayer, DeckType.Hand]);
                        }
                        cannotBeDiscarded = 0;
                        foreach (Card c in Game.CurrentGame.Decks[currentPlayer, DeckType.Hand])
                        {
                            if (!Game.CurrentGame.PlayerCanDiscardCard(currentPlayer, c))
                            {
                                cannotBeDiscarded++;
                            }
                        }

                        Trace.TraceInformation("Invalid answer, choosing for you");
                        cards = new List<Card>();
                        int cardsDiscarded = 0;
                        foreach (Card c in Game.CurrentGame.Decks[currentPlayer, DeckType.Hand])
                        {
                            if (Game.CurrentGame.PlayerCanDiscardCard(currentPlayer, c))
                            {
                                cards.Add(c);
                                cardsDiscarded++;
                            }
                            int cardsRemaining = Game.CurrentGame.Decks[currentPlayer, DeckType.Hand].Count - cardsDiscarded;
                            if (cardsRemaining <= Math.Max(currentPlayer.Health, cannotBeDiscarded))
                            {
                                break;
                            }
                        }
                    }
                    Game.CurrentGame.HandleCardDiscard(currentPlayer, cards);
                }
            }

            private class PlayerDiscardStageVerifier : ICardUsageVerifier
            {
                public UiHelper Helper { get { return new UiHelper(); } }

                public VerifierResult FastVerify(Player source, ISkill skill, List<Card> cards, List<Player> players)
                {
                    if (skill != null)
                    {
                        return VerifierResult.Fail;
                    }
                    if (players != null && players.Count > 0)
                    {
                        return VerifierResult.Fail;
                    }
                    if (cards == null || cards.Count == 0)
                    {
                        return VerifierResult.Partial;
                    }
                    foreach (Card c in cards)
                    {
                        if (!Game.CurrentGame.PlayerCanDiscardCard(source, c))
                        {
                            return VerifierResult.Fail;
                        }
                        if (c.Place.DeckType != DeckType.Hand)
                        {
                            return VerifierResult.Fail;
                        }
                    }
                    int cannotBeDiscarded = 0;
                    foreach (Card c in Game.CurrentGame.Decks[source, DeckType.Hand])
                    {
                        if (!Game.CurrentGame.PlayerCanDiscardCard(source, c))
                        {
                            cannotBeDiscarded++;
                        }
                    }
                    int remainingCards = (source.Health > cannotBeDiscarded) ? (source.Health) : cannotBeDiscarded;
                    if (Game.CurrentGame.Decks[source, DeckType.Hand].Count - cards.Count < remainingCards)
                    {
                        return VerifierResult.Fail;
                    }
                    return VerifierResult.Success;
                }

                public IList<CardHandler> AcceptableCardType
                {
                    get { throw new NotImplementedException(); }
                }

                public VerifierResult Verify(Player source, ISkill skill, List<Card> cards, List<Player> players)
                {
                    return FastVerify(source, skill, cards, players);
                }
            }
        }

        public class PlayerJudgeStageTrigger : Trigger
        {
            public override void Run(GameEvent gameEvent, GameEventArgs eventArgs)
            {
                Player currentPlayer = eventArgs.Game.CurrentPlayer;
                Trace.TraceInformation("Player {0} judge.", currentPlayer.Id);
                while (Game.CurrentGame.Decks[currentPlayer, DeckType.DelayedTools].Count > 0)
                {
                    Card card = Game.CurrentGame.Decks[currentPlayer, DeckType.DelayedTools].Last();
                    if (CardCategoryManager.IsCardCategory(card.Type.Category, CardCategory.DelayedTool))
                    {
                        DelayedTool tool = card.Type as DelayedTool;
                        tool.Activate(currentPlayer, card);
                    }
                }
            }
        }

        public class CommitActionToTargetsTrigger : Trigger
        {
            public override void Run(GameEvent gameEvent, GameEventArgs eventArgs)
            {
                List<Card> computeBackup;
                ICard c;
                if (eventArgs.Skill != null)
                {
                    CompositeCard card;
                    CardTransformSkill s = (CardTransformSkill)eventArgs.Skill;                    
                    if (!s.Transform(eventArgs.Cards, null, out card, eventArgs.Targets))
                    {
                        return;
                    }
                    c = card;
                }
                else
                {
                    Trace.Assert(eventArgs.Cards.Count == 1);
                    c = eventArgs.Cards[0];
                }

                // if it's delayed tool or equipment, we can't move it to compute area. call handlers directly
                if (CardCategoryManager.IsCardCategory(c.Type.Category, CardCategory.DelayedTool)
                    || CardCategoryManager.IsCardCategory(c.Type.Category, CardCategory.Equipment))
                {
                    c.Type.Process(eventArgs.Source, eventArgs.Targets, c);
                    return;
                }

                computeBackup = new List<Card>(Game.CurrentGame.Decks[DeckType.Compute]);
                Game.CurrentGame.Decks[DeckType.Compute].Clear();
                CardsMovement m;
                if (c is CompositeCard)
                {
                    m.cards = new List<Card>(((CompositeCard)c).Subcards);
                }
                else
                {
                    m.cards = new List<Card>(eventArgs.Cards);
                }
                m.to = new DeckPlace(null, DeckType.Compute);
                bool runTrigger = !c.Type.IsReforging(eventArgs.Source, eventArgs.Skill, m.cards, eventArgs.Targets);
                c.Type.TagAndNotify(eventArgs.Source, eventArgs.Targets, c);
                Game.CurrentGame.MoveCards(m, new CardUseLog() { Source = eventArgs.Source, Targets = eventArgs.Targets, Skill = eventArgs.Skill, Cards = eventArgs.Cards });
                Game.CurrentGame.PlayerLostCard(eventArgs.Source, eventArgs.Cards);
                Player savedSource = eventArgs.Source;

                if (runTrigger)
                {
                    try
                    {
                        GameEventArgs arg = new GameEventArgs();
                        arg.Source = eventArgs.Source;
                        arg.Targets = null;
                        arg.Card = c;

                        Game.CurrentGame.Emit(GameEvent.PlayerUsedCard, arg);
                    }
                    catch (TriggerResultException)
                    {
                        throw new NotImplementedException();
                    }
                }
                c.Type.Process(eventArgs.Source, eventArgs.Targets, c);

                m.cards = Game.CurrentGame.Decks[DeckType.Compute];
                m.to = new DeckPlace(null, DeckType.Discard);
                Game.CurrentGame.PlayerAboutToDiscardCard(savedSource, m.cards, DiscardReason.Use);
                Game.CurrentGame.MoveCards(m, null);
                Game.CurrentGame.PlayerDiscardedCard(savedSource, m.cards, DiscardReason.Use);
                Trace.Assert(Game.CurrentGame.Decks[DeckType.Compute].Count == 0);
                Game.CurrentGame.Decks[DeckType.Compute] = new List<Card>(computeBackup);
            }
        }

        private static void StartGameDeal(Game game)
        {
            List<CardsMovement> moves = new List<CardsMovement>();
            // Deal everyone 4 cards
            // todo: for testing
            foreach (Player player in game.Players)
            {
                CardsMovement move = new CardsMovement();
                move.cards = new List<Card>();
                move.to = new DeckPlace(player, DeckType.Hand);
                for (int i = 0; i < 8; i++)
                {
                    game.SyncCard(player, game.PeekCard(0));
                    Card c = game.DrawCard();
                    move.cards.Add(c);
                }
                moves.Add(move);
            }
            game.MoveCards(moves, null);
        }

        public static DeckType RoleDeckType = new DeckType("Role");

        public class RoleGameRuleTrigger : Trigger
        {
            public override void Run(GameEvent gameEvent, GameEventArgs eventArgs)
            {
                Game game = eventArgs.Game;
                int numberOfDefectors = 1;

                // Put the whole deck in the dealing deck
                game.Decks[DeckType.Dealing] = game.CardSet.GetRange(0, game.CardSet.Count);
                foreach (Card card in new List<Card>(game.Decks[DeckType.Dealing]))
                {
                    // We don't want hero cards
                    if (card.Type is HeroCardHandler)
                    {
                        game.Decks[DeckType.Dealing].Remove(card);
                        game.Decks[DeckType.Heroes].Add(card);
                        card.Place = new DeckPlace(null, DeckType.Heroes);
                    }
                    else
                    {
                        card.Place = new DeckPlace(null, DeckType.Dealing);
                    }
                }

                if (game.Players.Count == 0)
                {
                    return;
                }
                // Await role decision
                Random random = new Random(DateTime.Now.Millisecond);
                int rulerId = 0;

                game.Decks[null, RoleDeckType].Add(new Card(SuitType.None, 0, new RoleCardHandler(Role.Ruler)));
                Trace.Assert(game.Players.Count > 1);
                Trace.Assert(numberOfDefectors + 1 <= game.Players.Count);
                int t = numberOfDefectors;
                while (t-- > 0)
                {
                    game.Decks[null, RoleDeckType].Add(new Card(SuitType.None, 0, new RoleCardHandler(Role.Defector)));
                }
                int remaining = game.Players.Count - numberOfDefectors;
                int rebel;
                int loyalist;
                if (remaining <= 1)
                {
                    rebel = 0;
                    loyalist = 0;
                }
                else
                {
                    rebel = (int)Math.Ceiling(((double)remaining) / 2);
                    loyalist = remaining - rebel - 1;
                }

                Trace.Assert(rebel + loyalist + numberOfDefectors + 1 == game.Players.Count);
                (game as RoleGame).NumberOfDefectors = numberOfDefectors;
                (game as RoleGame).NumberOfRebels = rebel;

                while (rebel-- > 0)
                {
                    game.Decks[null, RoleDeckType].Add(new Card(SuitType.None, 0, new RoleCardHandler(Role.Rebel)));
                }
                while (loyalist-- > 0)
                {
                    game.Decks[null, RoleDeckType].Add(new Card(SuitType.None, 0, new RoleCardHandler(Role.Loyalist)));
                }


                foreach (Card c in game.Decks[null, RoleDeckType])
                {
                    c.Place = new DeckPlace(null, RoleDeckType);
                    c.Id = GameEngine.CardSet.Count;
                    GameEngine.CardSet.Add(c);
                }

                Shuffle(game.Decks[null, RoleDeckType]);

                if (game.IsClient)
                {
                    int count = game.Decks[null, RoleDeckType].Count;
                    game.Decks[null, RoleDeckType].Clear();
                    while (count-- > 0)
                    {
                        Card c = new Card(SuitType.None, 0, new UnknownRoleCardHandler());
                        c.Id = Card.UnknownRoleId;
                        c.Place = new DeckPlace(null, RoleDeckType);
                        game.Decks[null, RoleDeckType].Add(c);
                    }
                }

                if (!game.IsClient)
                {
                    foreach (Card c in game.Decks[null, RoleDeckType])
                    {
                        if ((c.Type as RoleCardHandler).Role == Role.Ruler)
                        {
                            game.SyncCardAll(c);
                        }
                    }
                }
                else
                {
                    game.SyncCardAll(game.Decks[null, RoleDeckType][0]);
                }

                int i = 0;
                for (i = 0; i < game.Players.Count; i++)
                {
                    game.SyncCard(game.Players[i], game.Decks[null, RoleDeckType][i]);
                    i++;
                }

                List<CardsMovement> moves = new List<CardsMovement>();
                i = 0;
                foreach (Player p in game.Players)
                {
                    CardsMovement move = new CardsMovement();
                    move.cards = new List<Card>() { game.Decks[null, RoleDeckType][i] };
                    move.to = new DeckPlace(p, RoleDeckType);
                    moves.Add(move);
                    i++;
                }
                game.MoveCards(moves, null);

                Thread.Sleep(500);

                i = 0;
                foreach (Player player in game.Players)
                {
                    Card card = game.Decks[player, RoleDeckType][0];
                    var role = card.Type as RoleCardHandler;
                    if (role != null)
                    {
                        if (role.Role == Role.Ruler)
                        {
                            rulerId = i;
                        }
                        player.Role = role.Role;
                    }
                    i++;
                }
                
                //hero allocation
                Shuffle(game.Decks[DeckType.Heroes]);
                if (!game.IsClient)
                {
                    foreach (var hero in new List<Card>(game.Decks[DeckType.Heroes]))
                    {
                        foreach (var s in (hero.Type as HeroCardHandler).Hero.Skills)
                        {
                            if (s.isRulerOnly)
                            {
                                game.Decks[DeckType.Heroes].Remove(hero);
                                game.Decks[DeckType.Heroes].Insert(0, hero);
                            }
                        }
                    }
                }
                List<Card> rulerDraw = new List<Card>();
                rulerDraw.Add(game.Decks[DeckType.Heroes][0]);
                rulerDraw.Add(game.Decks[DeckType.Heroes][1]);
                rulerDraw.Add(game.Decks[DeckType.Heroes][2]);
                rulerDraw.Add(game.Decks[DeckType.Heroes][3]);
                rulerDraw.Add(game.Decks[DeckType.Heroes][4]);
                rulerDraw.Add(game.Decks[DeckType.Heroes][5]);
                rulerDraw.Add(game.Decks[DeckType.Heroes][6]);
                rulerDraw.Add(game.Decks[DeckType.Heroes][7]);
                game.SyncCards(game.Players[rulerId], rulerDraw);
                DeckType tempHero = new DeckType("TempHero");
                game.Decks[null, tempHero].AddRange(rulerDraw);
                Trace.TraceInformation("Ruler is {0}", rulerId);
                game.Players[rulerId].Role = Role.Ruler;
                List<DeckPlace> sourceDecks = new List<DeckPlace>();
                sourceDecks.Add(new DeckPlace(null, tempHero));
                List<string> resultDeckNames = new List<string>();
                resultDeckNames.Add("HeroChoice");
                List<int> resultDeckMaximums = new List<int>();
                resultDeckMaximums.Add(1);
                List<List<Card>> answer;
                if (!game.UiProxies[game.Players[rulerId]].AskForCardChoice(new CardChoicePrompt("RulerHeroChoice"), sourceDecks, resultDeckNames, resultDeckMaximums, new AlwaysTrueChoiceVerifier(), out answer, new List<bool>() { false }))
                {
                    answer = new List<List<Card>>();
                    answer.Add(new List<Card>());
                    answer[0].Add(game.Decks[DeckType.Heroes][0]);
                }
                game.SyncCardAll(answer[0][0]);
                game.Decks[DeckType.Heroes].Remove(answer[0][0]);

                HeroCardHandler h = (HeroCardHandler)answer[0][0].Type;
                Trace.TraceInformation("Assign {0} to player {1}", h.Hero.Name, rulerId);
                Game.CurrentGame.Players[rulerId].Hero = h.Hero;
                Game.CurrentGame.Players[rulerId].Allegiance = h.Hero.Allegiance;
                Game.CurrentGame.Players[rulerId].MaxHealth = Game.CurrentGame.Players[rulerId].Health = h.Hero.MaxHealth;
                Game.CurrentGame.Players[rulerId].IsMale = h.Hero.IsMale ? true : false;
                Game.CurrentGame.Players[rulerId].IsFemale = h.Hero.IsMale ? false : true;


                Shuffle(game.Decks[DeckType.Heroes]);
                Dictionary<Player, List<Card>> restDraw = new Dictionary<Player, List<Card>>();
                List<Player> players = new List<Player>(game.Players);
                players.Remove(game.Players[rulerId]);
                int idx = 0;
                foreach (Player p in players)
                {
                    restDraw.Add(p, new List<Card>());
                    for (int n = 0; n < 3; n++)
                    {
                        game.SyncCard(p, game.Decks[DeckType.Heroes][idx]);
                        restDraw[p].Add(game.Decks[DeckType.Heroes][idx]);
                        idx++;
                    }
                }

                var heroSelection = new Dictionary<Player, Card>();
                game.GlobalProxy.AskForHeroChoice(restDraw, heroSelection);

                bool notUsed = true;
                game.SyncConfirmationStatus(ref notUsed);

                List<Card> toRemove = new List<Card>();
                foreach (Player p in players)
                {
                    Card c;
                    //only server has the result
                    if (!game.IsClient)
                    {
                        idx = 0;
                        if (heroSelection.ContainsKey(p))
                        {
                            c = heroSelection[p];
                            idx = restDraw[p].IndexOf(c);
                        }
                        else
                        {
                            c = restDraw[p][0];
                        }
                        foreach (Player player in game.Players)
                        {
                            game.GameServer.SendObject(player.Id, idx);
                        }
                    }
                    // you are client
                    else
                    {
                        idx = (int)game.GameClient.Receive();
                        c = restDraw[p][idx];
                    }
                    game.SyncCardAll(c);
                    toRemove.Add(c);
                    h = (HeroCardHandler)c.Type;
                    Trace.TraceInformation("Assign {0} to player {1}", h.Hero.Name, p.Id);
                    foreach (var skill in new List<ISkill>(h.Hero.Skills))
                    {
                        if (skill.isRulerOnly)
                        {
                            h.Hero.Skills.Remove(skill);
                        }
                    }
                    p.Hero = h.Hero;
                    p.Allegiance = h.Hero.Allegiance;
                    p.MaxHealth = p.Health = h.Hero.MaxHealth;
                    p.IsMale = h.Hero.IsMale ? true : false;
                    p.IsFemale = h.Hero.IsMale ? false : true;
                    
                }

                foreach (var card in toRemove)
                {
                    game.Decks[DeckType.Heroes].Remove(card);
                }

                Shuffle(game.Decks[null, DeckType.Dealing]);

                StartGameDeal(game);
                game.CurrentPlayer = game.Players[rulerId];
                game.CurrentPhase = TurnPhase.BeforeStart;

                while (true)
                {
                    game.Advance();
                }
            }
        }

        public RoleGame(int numberOfDefectors)
        {
            Trace.Assert(numberOfDefectors <= 2 && numberOfDefectors >= 0);
            defectorsCount = numberOfDefectors;
        }


        int defectorsCount;

        public static void Shuffle(IList<Card> list)
        {
            Random rng = new Random();
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                Card value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }

        private class PlayerIsDead : Trigger
        {
            public override void Run(GameEvent gameEvent, GameEventArgs eventArgs)
            {
                Player p = eventArgs.Targets[0];
                if (p.Hero != null)
                {
                    foreach (ISkill s in p.Hero.Skills)
                    {
                        if (s is PassiveSkill)
                        {
                            (s as PassiveSkill).Owner = null;
                        }
                    }
                }
                if (p.Hero2 != null)
                {
                    foreach (ISkill s in p.Hero2.Skills)
                    {
                        if (s is PassiveSkill)
                        {
                            (s as PassiveSkill).Owner = null;
                        }
                    }
                }
                Player source = eventArgs.Source;
                Trace.TraceInformation("Player {0} killed by Player {1}", p.Id, source.Id);
                DeckType role = new DeckType("Role");
                Trace.Assert(Game.CurrentGame.Decks[p, role].Count == 1);
                Game.CurrentGame.SyncCardAll(Game.CurrentGame.Decks[p, role][0]);
                Trace.TraceInformation("Player {0} is {1}", p.Id, (Game.CurrentGame.Decks[p, role][0].Type as RoleCardHandler).Role);
                p.Role = (Game.CurrentGame.Decks[p, role][0].Type as RoleCardHandler).Role;

                if (p.Role == Role.Ruler)
                {
                    Trace.TraceInformation("Ruler dead. Game over");
                    throw new GameOverException();
                }
                if (p.Role == Role.Rebel || p.Role == Role.Defector)
                {
                    int deadRebel = 0;
                    int deadDefector = 0;
                    foreach (Player z in Game.CurrentGame.Players)
                    {
                        if (z.Role == Role.Rebel && z.IsDead)
                        {
                            deadRebel++;
                        }
                        if (z.Role == Role.Defector && z.IsDead)
                        {
                            deadDefector++;
                        }
                    }
                    Trace.TraceInformation("Deathtoll: Rebel {0}/{1}, Defector {2}/{3}", deadRebel, (Game.CurrentGame as RoleGame).NumberOfRebels, deadDefector, (Game.CurrentGame as RoleGame).NumberOfDefectors);
                    if (deadRebel == (Game.CurrentGame as RoleGame).NumberOfRebels && deadDefector == (Game.CurrentGame as RoleGame).NumberOfDefectors)
                    {
                        Trace.TraceInformation("Ruler wins.");
                        throw new GameOverException();
                    }
                    if (!source.IsDead)
                    {
                        Trace.TraceInformation("Killed rebel. GIVING YOU THREE CARDS OMG WIN GAME RIGHT THERE!!!");
                        Game.CurrentGame.DrawCards(source, 3);
                    }
                }
                if (p.Role == Role.Loyalist && source.Role == Role.Ruler)
                {
                    Trace.TraceInformation("Loyalist killl by ruler. GG");
                    Game.CurrentGame.SyncCardsAll(Game.CurrentGame.Decks[source, DeckType.Hand]);
                    CardsMovement move = new CardsMovement();
                    move.cards = new List<Card>();
                    foreach (Card c in Game.CurrentGame.Decks[source, DeckType.Hand])
                    {
                        if (Game.CurrentGame.PlayerCanDiscardCard(source, c))
                        {
                            move.cards.Add(c);
                        }
                    }
                    move.cards.AddRange(Game.CurrentGame.Decks[source, DeckType.Equipment]);
                    move.cards.AddRange(Game.CurrentGame.Decks[source, DeckType.DelayedTools]);
                    move.to = new DeckPlace(null, DeckType.Discard);
                    Game.CurrentGame.MoveCards(move, null);
                }
            }
        }

        private class DeadManStopper : Trigger
        {
            public override void Run(GameEvent gameEvent, GameEventArgs eventArgs)
            {
                Trace.Assert(eventArgs.Targets.Count == 1);
                if (eventArgs.Targets[0].IsDead)
                {
                    Trace.TraceInformation("RIP {0}", eventArgs.Targets[0].Id);
                    throw new TriggerResultException(TriggerResult.Fail);
                }
            }
        }

        protected override void InitTriggers()
        {
            RegisterTrigger(GameEvent.GameStart, new RoleGameRuleTrigger());
            RegisterTrigger(GameEvent.PhaseProceedEvents[TurnPhase.Judge], new PlayerJudgeStageTrigger());
            RegisterTrigger(GameEvent.PhaseProceedEvents[TurnPhase.Play], new PlayerActionTrigger());
            RegisterTrigger(GameEvent.PhaseProceedEvents[TurnPhase.Draw], new PlayerDealStageTrigger());
            RegisterTrigger(GameEvent.PhaseProceedEvents[TurnPhase.Discard], new PlayerDiscardStageTrigger());
            RegisterTrigger(GameEvent.CommitActionToTargets, new CommitActionToTargetsTrigger());
            RegisterTrigger(GameEvent.AfterHealthChanged, new PlayerHpChanged());
            RegisterTrigger(GameEvent.PlayerIsDead, new PlayerIsDead());
            RegisterTrigger(GameEvent.PlayerIsCardTarget, new DeadManStopper() { Priority = int.MaxValue });
            RegisterTrigger(GameEvent.PlayerIsCardTarget, new DeadManStopper() { Priority = int.MinValue });
        }
    }

}
